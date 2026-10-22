namespace Crawler.Domain.Jobs;

/// <summary>A normalized outgoing link found on a page, and what should happen to it.</summary>
public record DiscoveredLink(string Url, bool IsInternal, CrawlPageStatus Status, string? StatusReason);

/// <summary>The final state of a crawled page.</summary>
public record PageOutcome(
    CrawlPageStatus Status,
    int? HttpStatusCode,
    double? DomainLinkRatio,
    int? OutgoingLinkCount,
    string? StatusReason);

/// <summary>
/// Everything learned from one page. Sitemap links become child pages but not link edges,
/// because the page itself doesn't link to them (and they don't count in its ratio).
/// </summary>
public record PageResult(
    PageOutcome Outcome,
    IReadOnlyList<DiscoveredLink> Links,
    IReadOnlyList<DiscoveredLink> SitemapLinks,
    DiscoveredLink? RedirectTarget)
{
    public static PageResult Without(PageOutcome outcome) => new(outcome, [], [], null);
}

/// <summary>A Pending page that needs a crawl message.</summary>
public class QueuedPage
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public int Depth { get; set; }
}

public static class StatusReasons
{
    /// <summary>Matches the varchar(2000) reason columns; reasons can embed URLs up to 2,048 characters.</summary>
    public const int MaxLength = 2000;

    public static string? Fit(string? reason) =>
        reason is { Length: > MaxLength } ? reason[..(MaxLength - 1)] + "…" : reason;
}

public static class SkipReasons
{
    public const string External = "External link";
    public const string BeyondMaxDepth = "Beyond max depth";
    public const string PageLimitReached = "Page limit reached";

    /// <summary>A redirect target already crawled under the URL that redirected to it.</summary>
    public static string SamePageAs(string url) => $"Same page as {url}";
}
