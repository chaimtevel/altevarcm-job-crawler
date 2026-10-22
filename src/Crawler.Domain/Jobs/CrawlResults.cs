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

/// <summary>A page row inserted while saving a result; Pending ones still need a crawl message.</summary>
public record NewPage(long Id, string Url, string Status);
