using Crawler.Domain.Crawling;
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

    /// <summary>Longer than one fetch plus save; an expired claim means the worker crashed.</summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    /// <summary>Claims a Pending page for crawling; false if another delivery holds a live claim.</summary>
    public async Task<bool> TryClaimPageAsync(long pageId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = now - ClaimLease;
        var claimed = await db.Pages
            .Where(p => p.Id == pageId
                        && p.Status == CrawlPageStatus.Pending
                        && (p.ClaimedAt == null || p.ClaimedAt < expired))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClaimedAt, now), ct);
        return claimed == 1;
    }

    /// <summary>Releases a claim after a failure so the message retry can crawl the page.</summary>
    public Task ReleaseClaimAsync(long pageId, CancellationToken ct) =>
        db.Pages
            .Where(p => p.Id == pageId && p.Status == CrawlPageStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClaimedAt, (DateTimeOffset?)null), ct);

    /// <summary>Pending children of this parent whose crawl message was never published.</summary>
    public async Task<IReadOnlyList<QueuedPage>> GetUnenqueuedChildrenAsync(long parentPageId, CancellationToken ct) =>
        await db.Pages.AsNoTracking()
            .Where(p => p.DiscoveredByPageId == parentPageId
                        && p.Status == CrawlPageStatus.Pending
                        && p.EnqueuedAt == null)
            .Select(p => new QueuedPage { Id = p.Id, Url = p.Url, Depth = p.Depth })
            .ToListAsync(ct);

    public Task MarkEnqueuedAsync(IReadOnlyList<QueuedPage> pages, CancellationToken ct)
    {
        if (pages.Count == 0)
        {
            return Task.CompletedTask;
        }

        var ids = pages.Select(p => p.Id).ToArray();
        var now = clock.GetUtcNow();
        return db.Pages
            .Where(p => ids.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.EnqueuedAt, now), ct);
    }

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
    /// Saves a page's outcome and its outgoing links, enforcing the job's page limit,
    /// then completes the job if nothing is pending. Returns the children that now need a crawl message.
    /// </summary>
    public Task<IReadOnlyList<QueuedPage>> SaveResultAsync(
        CrawlJob job,
        CrawlPage page,
        PageOutcome outcome,
        IReadOnlyList<DiscoveredLink> links,
        DiscoveredLink? redirectTarget,
        CancellationToken ct)
    {
        var childDepth = page.Depth + 1;
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();

            // Job row lock serializes saves per job: page-limit and completion checks can't race.
            var lockedRows = await db.Database.SqlQuery<int>(
                $"""SELECT reserved_page_count AS "Value" FROM crawl_jobs WHERE id = {job.Id} FOR UPDATE""")
                .ToListAsync(ct);
            if (lockedRows.Count == 0)
            {
                // Job was deleted meanwhile; nothing to save.
                return (IReadOnlyList<QueuedPage>)[];
            }

            if (redirectTarget is not null)
            {
                await MarkRedirectTargetAsync(job.Id, page, redirectTarget, now, ct);
            }

            var knownUrls = await GetSlottedUrlsAsync(job.Id, links, childDepth, ct);
            var (limitedLinks, reserved) = PageLimit.Apply(links, knownUrls, job.MaxPages - lockedRows[0]);

            if (reserved > 0)
            {
                await db.Jobs
                    .Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.ReservedPageCount, j => j.ReservedPageCount + reserved), ct);
            }

            var queued = await UpsertChildPagesAsync(job.Id, page.Id, childDepth, limitedLinks, now, ct);
            await InsertLinksAsync(job.Id, page.Id, limitedLinks, ct);
            await UpdatePageAsync(page.Id, outcome, now, ct);

            if (page.Depth == 0 && outcome.Status == CrawlPageStatus.Failed)
            {
                await FailJobAsync(job.Id, $"Start page could not be crawled: {outcome.StatusReason}", now, ct);
            }
            else
            {
                await CompleteJobIfDoneAsync(job.Id, now, ct);
            }

            await tx.CommitAsync(ct);
            return queued;
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
            {
                return 0;
            }

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

    /// <summary>
    /// Records the redirect target as this page, so links to it are known and never crawled.
    /// An unclaimed Pending or beyond-depth row for it is turned into the same marker.
    /// </summary>
    private Task MarkRedirectTargetAsync(
        Guid jobId, CrawlPage page, DiscoveredLink target, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO crawl_pages AS p (job_id, url, depth, discovered_by_page_id, is_internal, status, status_reason, discovered_at)
            VALUES ({jobId}, {target.Url}, {page.Depth}, {page.Id}, {target.IsInternal}, {target.Status.ToString()}, {target.StatusReason}, {now})
            ON CONFLICT (job_id, url) DO UPDATE SET
                status = EXCLUDED.status,
                status_reason = EXCLUDED.status_reason
            WHERE (p.status = 'Pending' AND p.claimed_at IS NULL)
               OR (p.status = 'Skipped' AND p.status_reason = {SkipReasons.BeyondMaxDepth})
            """, ct);

    /// <summary>Known URLs that need no new slot; a beyond-depth row reachable at a shallower depth needs one.</summary>
    private async Task<IReadOnlySet<string>> GetSlottedUrlsAsync(
        Guid jobId, IReadOnlyList<DiscoveredLink> links, int childDepth, CancellationToken ct)
    {
        var urls = links.Select(l => l.Url).ToArray();
        var known = await db.Pages
            .Where(p => p.JobId == jobId
                        && urls.Contains(p.Url)
                        && !(p.Status == CrawlPageStatus.Skipped
                             && p.StatusReason == SkipReasons.BeyondMaxDepth
                             && p.Depth > childDepth))
            .Select(p => p.Url)
            .ToListAsync(ct);
        return known.ToHashSet();
    }

    /// <summary>
    /// Inserts new child pages. An uncrawled existing page reached by a shorter path takes the shallower
    /// depth and this parent; if it was skipped as beyond max depth it gets this link's status.
    /// </summary>
    private async Task<IReadOnlyList<QueuedPage>> UpsertChildPagesAsync(
        Guid jobId, long parentId, int depth, IReadOnlyList<DiscoveredLink> links, DateTimeOffset now, CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return [];
        }

        var urls = links.Select(l => l.Url).ToArray();
        var internals = links.Select(l => l.IsInternal).ToArray();
        var statuses = links.Select(l => l.Status.ToString()).ToArray();
        var reasons = links.Select(l => l.StatusReason).ToArray();

        var rows = await db.Database.SqlQuery<UpsertedPage>($"""
            INSERT INTO crawl_pages AS p (job_id, url, depth, discovered_by_page_id, is_internal, status, status_reason, discovered_at)
            SELECT {jobId}, c.url, {depth}, {parentId}, c.is_internal, c.status, c.reason, {now}
            FROM unnest({urls}, {internals}, {statuses}, {reasons}) AS c(url, is_internal, status, reason)
            ON CONFLICT (job_id, url) DO UPDATE SET
                depth = EXCLUDED.depth,
                discovered_by_page_id = EXCLUDED.discovered_by_page_id,
                status = CASE WHEN p.status = 'Skipped' THEN EXCLUDED.status ELSE p.status END,
                status_reason = CASE WHEN p.status = 'Skipped' THEN EXCLUDED.status_reason ELSE p.status_reason END
            WHERE EXCLUDED.depth < p.depth
              AND (p.status = 'Pending' OR (p.status = 'Skipped' AND p.status_reason = {SkipReasons.BeyondMaxDepth}))
            RETURNING p.id, p.url, p.depth, p.status, (p.enqueued_at IS NULL) AS unsent
            """).ToListAsync(ct);

        return rows
            .Where(r => r.Status == nameof(CrawlPageStatus.Pending) && r.Unsent)
            .Select(r => new QueuedPage { Id = r.Id, Url = r.Url, Depth = r.Depth })
            .ToList();
    }

    // Single-word columns so the raw SQL maps regardless of naming convention.
    private class UpsertedPage
    {
        public long Id { get; set; }
        public string Url { get; set; } = "";
        public int Depth { get; set; }
        public string Status { get; set; } = "";
        public bool Unsent { get; set; }
    }

    private Task InsertLinksAsync(Guid jobId, long parentPageId, IReadOnlyList<DiscoveredLink> links, CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return Task.CompletedTask;
        }

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
