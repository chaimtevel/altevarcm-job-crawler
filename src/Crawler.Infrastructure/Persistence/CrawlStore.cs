using Crawler.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Persistence;

/// <summary>Worker-side persistence. Every write is idempotent so redelivered messages are harmless.</summary>
public class CrawlStore(CrawlerDbContext db, TimeProvider clock)
{
    public Task<CrawlJob?> GetJobAsync(Guid jobId, CancellationToken ct) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);

    public Task<CrawlPage?> GetPageAsync(long pageId, CancellationToken ct) =>
        db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId, ct);

    /// <summary>Pending → Running; no-op if already started.</summary>
    public Task MarkJobRunningAsync(Guid jobId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Jobs
            .Where(j => j.Id == jobId && j.Status == CrawlJobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, CrawlJobStatus.Running)
                .SetProperty(j => j.StartedAt, now), ct);
    }

    /// <summary>
    /// Saves a page's outcome and its outgoing links, then completes the job if nothing is pending.
    /// Returns the child pages this call inserted (none on a redelivery).
    /// </summary>
    public Task<IReadOnlyList<NewPage>> SaveResultAsync(
        CrawlJob job, CrawlPage page, PageOutcome outcome, IReadOnlyList<DiscoveredLink> links, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();

            // Serializes result-saving per job so the "nothing pending" completion check can't race.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM crawl_jobs WHERE id = {job.Id} FOR UPDATE", ct);

            var inserted = await InsertChildPagesAsync(job.Id, page, links, now, ct);
            await InsertLinksAsync(job.Id, page.Id, links, ct);
            await UpdatePageAsync(page.Id, outcome, now, ct);

            if (page.Depth == 0 && outcome.Status == CrawlPageStatus.Failed)
                await FailJobAsync(job.Id, $"Start page could not be crawled: {outcome.StatusReason}", now, ct);
            else
                await CompleteJobIfDoneAsync(job.Id, now, ct);

            await tx.CommitAsync(ct);
            return inserted;
        });
    }

    /// <summary>Fails jobs past their deadline and skips their pending pages. Returns the jobs timed out.</summary>
    public Task<int> TimeOutExpiredJobsAsync(CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var cutoff = now - CrawlJob.Timeout;
            var reason = $"Timed out after {CrawlJob.Timeout.TotalMinutes:0} minutes";

            // Lock job rows first (same order as SaveResultAsync) to avoid deadlocks.
            var expiredJobIds = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM crawl_jobs
                WHERE status IN ('Pending', 'Running') AND created_at < {cutoff}
                ORDER BY id
                FOR UPDATE
                """).ToListAsync(ct);

            if (expiredJobIds.Count == 0)
                return 0;

            await db.Pages
                .Where(p => p.Status == CrawlPageStatus.Pending && expiredJobIds.Contains(p.JobId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, CrawlPageStatus.Skipped)
                    .SetProperty(p => p.StatusReason, "Job timed out"), ct);

            var timedOut = await db.Jobs
                .Where(j => expiredJobIds.Contains(j.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, CrawlJobStatus.Failed)
                    .SetProperty(j => j.FailureReason, reason)
                    .SetProperty(j => j.CompletedAt, now), ct);

            await tx.CommitAsync(ct);
            return timedOut;
        });
    }

    private async Task<IReadOnlyList<NewPage>> InsertChildPagesAsync(
        Guid jobId, CrawlPage parent, IReadOnlyList<DiscoveredLink> links, DateTimeOffset now, CancellationToken ct)
    {
        if (links.Count == 0)
            return [];

        var urls = links.Select(l => l.Url).ToArray();
        var internals = links.Select(l => l.IsInternal).ToArray();
        var statuses = links.Select(l => l.Status.ToString()).ToArray();
        var reasons = links.Select(l => l.StatusReason).ToArray();
        var depth = parent.Depth + 1;

        // ON CONFLICT: a URL already known to this job keeps its first-discovered row.
        return await db.Database.SqlQuery<NewPage>($"""
            INSERT INTO crawl_pages (job_id, url, depth, is_internal, status, status_reason, discovered_at)
            SELECT {jobId}, c.url, {depth}, c.is_internal, c.status, c.reason, {now}
            FROM unnest({urls}, {internals}, {statuses}, {reasons}) AS c(url, is_internal, status, reason)
            ON CONFLICT (job_id, url) DO NOTHING
            RETURNING id, url, status
            """).ToListAsync(ct);
    }

    private Task InsertLinksAsync(Guid jobId, long parentPageId, IReadOnlyList<DiscoveredLink> links, CancellationToken ct)
    {
        if (links.Count == 0)
            return Task.CompletedTask;

        var urls = links.Select(l => l.Url).ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO page_links (job_id, parent_page_id, child_page_id)
            SELECT {jobId}, {parentPageId}, p.id
            FROM crawl_pages p
            WHERE p.job_id = {jobId} AND p.url = ANY({urls})
            ON CONFLICT DO NOTHING
            """, ct);
    }

    private Task UpdatePageAsync(long pageId, PageOutcome outcome, DateTimeOffset now, CancellationToken ct) =>
        db.Pages
            .Where(p => p.Id == pageId && p.Status == CrawlPageStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, outcome.Status)
                .SetProperty(p => p.StatusReason, outcome.StatusReason)
                .SetProperty(p => p.HttpStatusCode, outcome.HttpStatusCode)
                .SetProperty(p => p.DomainLinkRatio, outcome.DomainLinkRatio)
                .SetProperty(p => p.OutgoingLinkCount, outcome.OutgoingLinkCount)
                .SetProperty(p => p.CrawledAt, now), ct);

    private Task CompleteJobIfDoneAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
        db.Jobs
            .Where(j => j.Id == jobId
                        && j.Status == CrawlJobStatus.Running
                        && !db.Pages.Any(p => p.JobId == jobId && p.Status == CrawlPageStatus.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, CrawlJobStatus.Completed)
                .SetProperty(j => j.CompletedAt, now), ct);

    private Task FailJobAsync(Guid jobId, string reason, DateTimeOffset now, CancellationToken ct) =>
        db.Jobs
            .Where(j => j.Id == jobId && (j.Status == CrawlJobStatus.Pending || j.Status == CrawlJobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, CrawlJobStatus.Failed)
                .SetProperty(j => j.FailureReason, reason)
                .SetProperty(j => j.CompletedAt, now), ct);
}
