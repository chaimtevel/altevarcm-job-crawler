using Crawler.Domain.Jobs;

namespace Crawler.Api.Jobs;

public record CreateJobRequest(string? Url, int? MaxDepth);

public record CreateJobResponse(Guid JobId);

public record JobProgress(int Discovered, int Pending, int Crawled, int Failed, int Skipped);

public record JobDetailsResponse(
    Guid JobId,
    string Url,
    int MaxDepth,
    int MaxPages,
    CrawlJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    JobProgress Progress);

public record JobSummary(
    Guid JobId,
    string Url,
    CrawlJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
