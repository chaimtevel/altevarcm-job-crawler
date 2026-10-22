using System.Diagnostics;
using System.Text.Json;
using Crawler.Domain.Jobs;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Crawler.Api.Jobs;

public static class JobEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxPage = 100_000;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var jobs = routes.MapGroup("/api/jobs").WithTags("Jobs");

        jobs.MapPost("/", CreateJob).WithName(nameof(CreateJob));
        jobs.MapGet("/{jobId:guid}", GetJob).WithName(nameof(GetJob));
        jobs.MapGet("/{jobId:guid}/events", StreamJobEvents).WithName(nameof(StreamJobEvents)).Produces(200, contentType: "text/event-stream");
        jobs.MapGet("/", ListJobs).WithName(nameof(ListJobs));
        jobs.MapGet("/{jobId:guid}/tree", GetJobTree).WithName(nameof(GetJobTree));
        jobs.MapGet("/{jobId:guid}/pages/{pageId:long}/links", GetPageLinks).WithName(nameof(GetPageLinks));
        jobs.MapPost("/{jobId:guid}/cancel", CancelJob).WithName(nameof(CancelJob));

        return routes;
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> CancelJob(
        Guid jobId,
        CrawlStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var result = await store.CancelJobAsync(jobId, ct);
        if (result == CancelJobResult.NotFound)
        {
            return TypedResults.NotFound();
        }

        if (result == CancelJobResult.AlreadyFinished)
        {
            return TypedResults.Problem("Only pending or running jobs can be canceled.", statusCode: StatusCodes.Status409Conflict);
        }

        loggerFactory.CreateLogger(typeof(JobEndpoints).FullName!).LogInformation("Canceled crawl job {JobId}", jobId);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Created<CreateJobResponse>, ValidationProblem, ProblemHttpResult>> CreateJob(
        CreateJobRequest request,
        CrawlerDbContext db,
        CrawlPagePublisher publisher,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(JobEndpoints).FullName!);

        var (startUrl, errors) = CreateJobValidator.Validate(request);
        if (startUrl is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var job = CrawlJob.Create(
            startUrl,
            request.MaxDepth ?? CrawlJob.DefaultMaxDepth,
            CrawlJob.DefaultMaxPages,
            clock.GetUtcNow());
        job.ReservedPageCount = 1;
        var root = CrawlPage.Root(job);

        // Job and root page are saved atomically in one SaveChanges.
        db.Jobs.Add(job);
        db.Pages.Add(root);
        await db.SaveChangesAsync(ct);

        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["CorrelationId"] = correlationId });

        try
        {
            // The job is saved, so finish publishing even if the client disconnects.
            var queued = new QueuedPage { Id = root.Id, Url = root.Url, Depth = root.Depth };
            await publisher.PublishAsync(job, [queued], correlationId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // No outbox yet: fail the job visibly rather than leave it Pending forever.
            logger.LogError(ex, "Could not publish start message for job {JobId}", job.Id);
            const string reason = "Could not queue the crawl; message broker unavailable.";
            var now = clock.GetUtcNow();
            job.Status = CrawlJobStatus.Failed;
            job.FailureReason = reason;
            job.CompletedAt = now;
            root.Status = CrawlPageStatus.Failed;
            root.StatusReason = reason;
            await db.SaveChangesAsync(CancellationToken.None);
            return TypedResults.Problem("The crawl could not be queued. Please try again.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Created crawl job {JobId} for {Url} with max depth {MaxDepth}", job.Id, job.Url, job.MaxDepth);

        return TypedResults.Created($"/api/jobs/{job.Id}", new CreateJobResponse(job.Id));
    }

    private static async Task<Results<Ok<JobDetailsResponse>, NotFound>> GetJob(
        Guid jobId,
        CrawlerDbContext db,
        CancellationToken ct)
    {
        var details = await LoadJobDetailsAsync(db, jobId, ct);
        return details is null ? TypedResults.NotFound() : TypedResults.Ok(details);
    }

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinPushInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Server-Sent Events: pushes the job details on connect and after each change the worker reports
    /// through Postgres NOTIFY. A keep-alive also refreshes, covering a missed notification. Ends when the job finishes.
    /// </summary>
    private static async Task StreamJobEvents(
        Guid jobId,
        HttpContext http,
        CrawlerDbContext db,
        JobProgressNotifications notifications,
        IOptions<JsonOptions> json,
        IHostApplicationLifetime lifetime,
        CancellationToken requestAborted)
    {
        // End on browser disconnect or API shutdown, so open streams don't delay a restart.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, lifetime.ApplicationStopping);
        var ct = stopping.Token;

        // Subscribe before the first read, so a change in between isn't missed.
        using var subscription = notifications.Subscribe(jobId);

        var details = await LoadJobDetailsAsync(db, jobId, ct);
        if (details is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        try
        {
            while (true)
            {
                var payload = JsonSerializer.Serialize(details, json.Value.SerializerOptions);
                await http.Response.WriteAsync($"data: {payload}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                if (!details.Status.IsActive())
                {
                    return;
                }

                if (await subscription.WaitAsync(KeepAliveInterval, ct))
                {
                    // Batch the burst of page saves into at most two pushes per second.
                    await Task.Delay(MinPushInterval, ct);
                }

                details = await LoadJobDetailsAsync(db, jobId, ct);
                if (details is null)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The browser closed the stream, or the API is shutting down.
        }
    }

    private static async Task<JobDetailsResponse?> LoadJobDetailsAsync(CrawlerDbContext db, Guid jobId, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return null;
        }

        // One GROUP BY over the (job_id, status) index instead of loading pages.
        var counts = await db.Pages
            .Where(p => p.JobId == jobId)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        int Count(CrawlPageStatus status) => counts.GetValueOrDefault(status);

        var progress = new JobProgress(
            Discovered: counts.Values.Sum(),
            Pending: Count(CrawlPageStatus.Pending),
            Crawled: Count(CrawlPageStatus.Crawled),
            Failed: Count(CrawlPageStatus.Failed),
            Skipped: Count(CrawlPageStatus.Skipped));

        return new JobDetailsResponse(
            job.Id, job.Url, job.MaxDepth, job.MaxPages, job.Status,
            job.CreatedAt, job.StartedAt, job.CompletedAt, job.FailureReason, progress);
    }

    private static async Task<Results<Ok<PagedResponse<JobSummary>>, ValidationProblem>> ListJobs(
        CrawlerDbContext db,
        CancellationToken ct,
        int page = 1,
        int pageSize = DefaultPageSize,
        string? search = null)
    {
        // Upper bounds also keep (page - 1) * pageSize from overflowing.
        if (page is < 1 or > MaxPage || pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paging"] = [$"page must be between 1 and {MaxPage}, pageSize between 1 and {MaxPageSize}."],
            });
        }

        // Case-insensitive "URL contains"; fine for history sizes, a trigram index would scale it further.
        var jobs = db.Jobs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Escape LIKE wildcards so "_" and "%" in the box match literally.
            var term = search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            jobs = jobs.Where(j => EF.Functions.ILike(j.Url, $"%{term}%", @"\"));
        }

        var totalCount = await jobs.CountAsync(ct);

        // Served by the created_at DESC index.
        var items = await jobs
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new JobSummary(j.Id, j.Url, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResponse<JobSummary>(items, page, pageSize, totalCount));
    }

    private static async Task<Results<Ok<JobTreeResponse>, NotFound>> GetJobTree(
        Guid jobId,
        CrawlerDbContext db,
        CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        // Only reserved pages (never-fetched Skipped links excluded), so size is bounded by MaxPages.
        // Skipped links remain available per page via the links endpoint.
        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.JobId == jobId && (p.Status != CrawlPageStatus.Skipped || p.CrawledAt != null))
            .ToListAsync(ct);

        return TypedResults.Ok(new JobTreeResponse(job.Id, job.Status, JobTreeBuilder.Build(pages)));
    }

    private static async Task<Results<Ok<IReadOnlyList<PageLinkResponse>>, NotFound>> GetPageLinks(
        Guid jobId,
        long pageId,
        CrawlerDbContext db,
        CancellationToken ct)
    {
        var pageExists = await db.Pages.AnyAsync(p => p.Id == pageId && p.JobId == jobId, ct);
        if (!pageExists)
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<PageLinkResponse> links = await db.Links
            .Where(l => l.JobId == jobId && l.ParentPageId == pageId)
            .Join(db.Pages, l => l.ChildPageId, p => p.Id, (l, p) => p)
            .OrderBy(p => p.Id)
            .Select(p => new PageLinkResponse(p.Id, p.Url, p.IsInternal, p.Status, p.StatusReason))
            .ToListAsync(ct);

        return TypedResults.Ok(links);
    }
}
