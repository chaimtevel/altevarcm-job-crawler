using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Persistence;

public enum CancelJobResult { Canceled, NotFound, AlreadyFinished }

/// <summary>Job and page state changes. Every write is idempotent so redelivered messages are harmless.</summary>
public class CrawlStore(CrawlerDbContext db, TimeProvider clock)
{
    public const string CanceledReason = "Job canceled";

    /// <summary>Postgres NOTIFY channel; payload is the job id. The API listens and pushes SSE updates.</summary>
    public const string ProgressChannel = "job_progress";

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
    public async Task MarkJobRunningAsync(Guid jobId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var started = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == CrawlJobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, CrawlJobStatus.Running)
                .SetProperty(j => j.StartedAt, now), ct);

        if (started == 1)
        {
            await NotifyProgressAsync([jobId], ct);
        }
    }

    /// <summary>Inside a transaction Postgres delivers this on commit, so listeners never see uncommitted state.</summary>
    private async Task NotifyProgressAsync(Guid[] jobIds, CancellationToken ct)
    {
        // One statement for any number of jobs (the timeout sweep can pass many).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify({ProgressChannel}, id::text) FROM unnest({jobIds}) AS id", ct);
    }

    /// <summary>
    /// Saves a page's outcome and its outgoing links, enforcing the job's page limit,
    /// then completes the job if nothing is pending. Returns the children that now need a crawl message,
    /// or null if the job finished meanwhile and the result was discarded.
    /// </summary>
    public Task<IReadOnlyList<QueuedPage>?> SaveResultAsync(
        CrawlJob job,
        CrawlPage page,
        PageResult result,
        CancellationToken ct)
    {
        var outcome = result.Outcome;
        var redirectTarget = result.RedirectTarget;
        // Page links first, so they win page-limit slots over sitemap URLs.
        var links = result.Links.Concat(result.SitemapLinks).ToList();
        var childDepth = page.Depth + 1;
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();

            // Job row lock serializes saves per job: page-limit and completion checks can't race.
            var locked = await db.Database.SqlQuery<LockedJob>(
                $"""SELECT reserved_page_count AS reserved, status AS status FROM crawl_jobs WHERE id = {job.Id} FOR UPDATE""")
                .ToListAsync(ct);
            if (locked.Count == 0 || !Enum.Parse<CrawlJobStatus>(locked[0].Status).IsActive())
            {
                // Deleted, canceled or timed out meanwhile: don't add new Pending pages to a finished job.
                return (IReadOnlyList<QueuedPage>?)null;
            }

            if (redirectTarget is not null)
            {
                await MarkRedirectTargetAsync(job.Id, page, redirectTarget, now, ct);
            }

            var knownUrls = await GetSlottedUrlsAsync(job.Id, links, childDepth, ct);
            var (limitedLinks, reserved) = PageLimit.Apply(links, knownUrls, job.MaxPages - locked[0].Reserved);

            if (reserved > 0)
            {
                await db.Jobs
                    .Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.ReservedPageCount, j => j.ReservedPageCount + reserved), ct);
            }

            // Sitemap URLs past the page limit are dropped rather than stored as Skipped rows.
            var pageLinkUrls = result.Links.Select(l => l.Url).ToHashSet();
            var children = limitedLinks
                .Where(l => pageLinkUrls.Contains(l.Url) || l.Status == CrawlPageStatus.Pending)
                .ToList();

            var queued = await UpsertChildPagesAsync(job.Id, page.Id, childDepth, children, now, ct);
            await InsertLinksAsync(job.Id, page.Id, children.Where(l => pageLinkUrls.Contains(l.Url)).ToList(), ct);
            await UpdatePageAsync(page.Id, outcome, now, ct);

            if (page.Depth == 0 && outcome.Status == CrawlPageStatus.Failed)
            {
                await FailJobAsync(job.Id, $"Start page could not be crawled: {outcome.StatusReason}", now, ct);
            }
            else
            {
                await CompleteJobIfDoneAsync(job.Id, now, ct);
            }

            await NotifyProgressAsync([job.Id], ct);
            await tx.CommitAsync(ct);
            return (IReadOnlyList<QueuedPage>?)queued;
        });
    }

    /// <summary>Cancels a Pending or Running job and skips its pending pages; queued messages are then ignored.</summary>
    public Task<CancelJobResult> CancelJobAsync(Guid jobId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Same job-row lock as SaveResultAsync: an in-flight save finishes first, later ones see Canceled.
            var statuses = await db.Database.SqlQuery<string>(
                $"""SELECT status AS "Value" FROM crawl_jobs WHERE id = {jobId} FOR UPDATE""")
                .ToListAsync(ct);
            if (statuses.Count == 0)
            {
                return CancelJobResult.NotFound;
            }

            if (!Enum.Parse<CrawlJobStatus>(statuses[0]).IsActive())
            {
                return CancelJobResult.AlreadyFinished;
            }

            await SkipPendingPagesAsync([jobId], CanceledReason, ct);

            var now = clock.GetUtcNow();
            await db.Jobs
                .Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, CrawlJobStatus.Canceled)
                    .SetProperty(j => j.CompletedAt, now), ct);

            await NotifyProgressAsync([jobId], ct);
            await tx.CommitAsync(ct);
            return CancelJobResult.Canceled;
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
                """).ToArrayAsync(ct);

            if (expiredJobIds.Length == 0)
            {
                return 0;
            }

            await SkipPendingPagesAsync(expiredJobIds, "Job timed out", ct);

            var timedOut = await db.Jobs
                .Where(j => expiredJobIds.Contains(j.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, CrawlJobStatus.Failed)
                    .SetProperty(j => j.FailureReason, reason)
                    .SetProperty(j => j.CompletedAt, now), ct);

            await NotifyProgressAsync(expiredJobIds, ct);
            await tx.CommitAsync(ct);
            return timedOut;
        });
    }

    /// <summary>Skips the unfetched pages of jobs being canceled or timed out; caller holds the job-row locks.</summary>
    private Task SkipPendingPagesAsync(IReadOnlyCollection<Guid> jobIds, string reason, CancellationToken ct) =>
        db.Pages
            .Where(p => p.Status == CrawlPageStatus.Pending && jobIds.Contains(p.JobId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, CrawlPageStatus.Skipped)
                .SetProperty(p => p.StatusReason, reason), ct);

    /// <summary>
    /// Records the redirect target as this page, so links to it are known and never crawled.
    /// An unclaimed Pending or beyond-depth row for it is turned into the same marker.
    /// </summary>
    private Task MarkRedirectTargetAsync(
        Guid jobId, CrawlPage page, DiscoveredLink target, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO crawl_pages AS p (job_id, url, depth, discovered_by_page_id, is_internal, status, status_reason, discovered_at)
            VALUES ({jobId}, {target.Url}, {page.Depth}, {page.Id}, {target.IsInternal}, {target.Status.ToString()}, {StatusReasons.Fit(target.StatusReason)}, {now})
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
    private class LockedJob
    {
        public int Reserved { get; set; }
        public string Status { get; set; } = "";
    }

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
                .SetProperty(p => p.StatusReason, StatusReasons.Fit(outcome.StatusReason))
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
                .SetProperty(j => j.FailureReason, StatusReasons.Fit(reason))
                .SetProperty(j => j.CompletedAt, now), ct);
}
