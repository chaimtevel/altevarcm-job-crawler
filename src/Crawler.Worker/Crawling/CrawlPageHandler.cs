using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;

namespace Crawler.Worker.Crawling;

/// <summary>Crawls one page per message. Safe to run twice for the same message.</summary>
public class CrawlPageHandler(
    CrawlStore store,
    PageFetcher fetcher,
    CrawlPagePublisher publisher,
    TimeProvider clock,
    ILogger<CrawlPageHandler> logger)
{
    public async Task HandleAsync(CrawlPageMessage message, CancellationToken ct)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = message.JobId,
            ["PageId"] = message.PageId,
            ["CorrelationId"] = message.CorrelationId,
        });

        var job = await store.GetJobAsync(message.JobId, ct);
        var page = await store.GetPageAsync(message.PageId, ct);

        if (job is null || page is null)
        {
            logger.LogWarning("Job or page not found; acknowledging message without work");
            return;
        }

        if (job.Status is CrawlJobStatus.Completed or CrawlJobStatus.Failed or CrawlJobStatus.Canceled)
        {
            logger.LogInformation("Job is {Status}; skipping page {Url}", job.Status, page.Url);
            return;
        }

        if (job.IsExpired(clock.GetUtcNow()))
        {
            // JobTimeoutService fails the job and skips its pending pages.
            logger.LogWarning("Job passed its deadline; skipping page {Url}", page.Url);
            return;
        }

        IReadOnlyList<QueuedPage> children;
        if (page.Status == CrawlPageStatus.Pending)
        {
            if (!await store.TryClaimPageAsync(page.Id, ct))
            {
                logger.LogInformation("Page {Url} is being crawled by another delivery; skipping", page.Url);
                return;
            }

            children = await CrawlAndSaveAsync(job, page, ct);
        }
        else
        {
            // Redelivery: the save committed, but some child messages may not have been published.
            children = await store.GetUnenqueuedChildrenAsync(page.Id, ct);
            logger.LogInformation("Page {Url} already {Status}; publishing {Count} unsent children", page.Url, page.Status, children.Count);
        }

        await publisher.PublishAsync(job, children, message.CorrelationId, ct);
        await store.MarkEnqueuedAsync(children, ct);
    }

    private async Task<IReadOnlyList<QueuedPage>> CrawlAndSaveAsync(CrawlJob job, CrawlPage page, CancellationToken ct)
    {
        try
        {
            await store.MarkJobRunningAsync(job.Id, ct);

            var (outcome, links) = await CrawlAsync(job, page, ct);
            var children = await store.SaveResultAsync(job, page, outcome, links, ct);

            logger.LogInformation(
                "Page {Url} at depth {Depth}: {Status}, {LinkCount} links, ratio {Ratio}",
                page.Url, page.Depth, outcome.Status, outcome.OutgoingLinkCount, outcome.DomainLinkRatio);
            return children;
        }
        catch
        {
            // Let the message retry crawl the page instead of waiting for the claim to expire.
            await store.ReleaseClaimAsync(page.Id, CancellationToken.None);
            throw;
        }
    }

    private async Task<(PageOutcome Outcome, IReadOnlyList<DiscoveredLink> Links)> CrawlAsync(
        CrawlJob job, CrawlPage page, CancellationToken ct)
    {
        var fetch = await fetcher.FetchAsync(new Uri(page.Url), ct);

        if (fetch.Status != FetchStatus.Html)
        {
            var status = fetch.Status == FetchStatus.NotHtml ? CrawlPageStatus.Skipped : CrawlPageStatus.Failed;
            return (new PageOutcome(status, fetch.HttpStatusCode, null, null, fetch.Reason), []);
        }

        var startHost = new Uri(job.Url).Host;
        var urls = HtmlLinkExtractor.ExtractLinks(fetch.Html!, fetch.FinalUrl!);
        var ratio = DomainLinkRatio.Calculate(urls, startHost);
        var links = urls.Select(url => Classify(url, startHost, page.Depth + 1, job.MaxDepth)).ToList();

        return (new PageOutcome(CrawlPageStatus.Crawled, fetch.HttpStatusCode, ratio, urls.Count, null), links);
    }

    /// <summary>Internal links within max depth are Pending; the store then applies the page limit.</summary>
    private static DiscoveredLink Classify(Uri url, string startHost, int childDepth, int maxDepth)
    {
        if (!DomainLinkRatio.IsWithinDomain(url.Host, startHost))
        {
            return new(url.AbsoluteUri, false, CrawlPageStatus.Skipped, SkipReasons.External);
        }

        if (childDepth > maxDepth)
        {
            return new(url.AbsoluteUri, true, CrawlPageStatus.Skipped, SkipReasons.BeyondMaxDepth);
        }

        return new(url.AbsoluteUri, true, CrawlPageStatus.Pending, null);
    }
}
