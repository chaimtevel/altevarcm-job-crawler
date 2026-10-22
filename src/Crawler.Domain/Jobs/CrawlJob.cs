namespace Crawler.Domain.Jobs;

public enum CrawlJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
}

public class CrawlJob
{
    public const int DefaultMaxDepth = 2;
    public const int MaxAllowedDepth = 5;
    public const int DefaultMaxPages = 200;

    /// <summary>Hard limit per job; well above a typical crawl. Also used as the message TTL.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public Guid Id { get; set; }
    public required string Url { get; set; }
    public int MaxDepth { get; set; }
    public int MaxPages { get; set; }
    public CrawlJobStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>
    /// Pages reserved for crawling so far. Incremented atomically against <see cref="MaxPages"/>
    /// so concurrent workers can never exceed the limit.
    /// </summary>
    public int ReservedPageCount { get; set; }

    public DateTimeOffset GetDeadline() => CreatedAt + Timeout;

    public bool IsExpired(DateTimeOffset now) => now >= GetDeadline();

    public static CrawlJob Create(Uri url, int maxDepth, int maxPages, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Url = url.AbsoluteUri,
        MaxDepth = maxDepth,
        MaxPages = maxPages,
        Status = CrawlJobStatus.Pending,
        CreatedAt = now,
    };
}
