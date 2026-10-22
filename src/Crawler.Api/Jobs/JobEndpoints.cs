using System.Diagnostics;
using Crawler.Domain.Jobs;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Dapr.Client;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
        jobs.MapGet("/", ListJobs).WithName(nameof(ListJobs));

        return routes;
    }

    private static async Task<Results<Created<CreateJobResponse>, ValidationProblem, ProblemHttpResult>> CreateJob(
        CreateJobRequest request,
        CrawlerDbContext db,
        DaprClient dapr,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(JobEndpoints).FullName!);

        var (startUrl, errors) = CreateJobValidator.Validate(request);
        if (startUrl is null)
            return TypedResults.ValidationProblem(errors);

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
            var message = new CrawlPageMessage(job.Id, root.Id, root.Url, root.Depth, correlationId);
            var metadata = CrawlMessaging.PublishMetadata(job.GetDeadline(), clock.GetUtcNow());
            // The job is saved, so finish publishing even if the client disconnects.
            await dapr.PublishEventAsync(CrawlMessaging.PubSubName, CrawlMessaging.CrawlPageTopic, message, metadata, CancellationToken.None);
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
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return TypedResults.NotFound();

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

        return TypedResults.Ok(new JobDetailsResponse(
            job.Id, job.Url, job.MaxDepth, job.MaxPages, job.Status,
            job.CreatedAt, job.StartedAt, job.CompletedAt, job.FailureReason, progress));
    }

    private static async Task<Results<Ok<PagedResponse<JobSummary>>, ValidationProblem>> ListJobs(
        CrawlerDbContext db,
        CancellationToken ct,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        // Upper bounds also keep (page - 1) * pageSize from overflowing.
        if (page is < 1 or > MaxPage || pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paging"] = [$"page must be between 1 and {MaxPage}, pageSize between 1 and {MaxPageSize}."],
            });
        }

        var totalCount = await db.Jobs.CountAsync(ct);

        // Served by the created_at DESC index.
        var items = await db.Jobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new JobSummary(j.Id, j.Url, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResponse<JobSummary>(items, page, pageSize, totalCount));
    }
}
