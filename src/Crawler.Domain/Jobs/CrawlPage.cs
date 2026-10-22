namespace Crawler.Domain.Jobs;

public enum CrawlPageStatus
{
    /// <summary>Queued for crawling; a message has been published for it.</summary>
    Pending,
    Crawled,
    Failed,
    /// <summary>Discovered but intentionally not crawled (external, beyond max depth, page limit, non-HTML).</summary>
    Skipped,
}

/// <summary>
/// A URL discovered within a job. Every outgoing link becomes a page row (crawled or not),
/// so the tree and each page's outgoing-link list can be rebuilt from pages + links.
/// </summary>
public class CrawlPage
{
    public long Id { get; set; }
    public Guid JobId { get; set; }

    /// <summary>Normalized absolute URL; unique per job.</summary>
    public required string Url { get; set; }

    public int Depth { get; set; }

    /// <summary>The page that first linked here; null for the root. Gives each page one tree parent.</summary>
    public long? DiscoveredByPageId { get; set; }

    public bool IsInternal { get; set; }
    public CrawlPageStatus Status { get; set; }
    public string? StatusReason { get; set; }
    public int? HttpStatusCode { get; set; }
    public double? DomainLinkRatio { get; set; }
    public int? OutgoingLinkCount { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; }
    public DateTimeOffset? CrawledAt { get; set; }

    /// <summary>Set while a worker crawls the page; stops concurrent duplicate deliveries.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Set once its crawl message is published; redeliveries republish only unset ones.</summary>
    public DateTimeOffset? EnqueuedAt { get; set; }

    /// <summary>The job's start page: depth 0, queued for crawling.</summary>
    public static CrawlPage Root(CrawlJob job) => new()
    {
        JobId = job.Id,
        Url = job.Url,
        Depth = 0,
        IsInternal = true,
        Status = CrawlPageStatus.Pending,
        DiscoveredAt = job.CreatedAt,
    };
}
