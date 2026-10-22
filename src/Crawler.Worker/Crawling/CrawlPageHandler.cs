using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;

namespace Crawler.Worker.Crawling;

/// <summary>Crawls one page per message. Safe to run twice for the same message.</summary>
public class CrawlPageHandler(CrawlStore store, PageFetcher fetcher, TimeProvider clock, ILogger<CrawlPageHandler> logger)
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

        if (page.Status != CrawlPageStatus.Pending)
        {
            logger.LogInformation("Page {Url} already processed ({Status}); duplicate delivery ignored", page.Url, page.Status);
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

        await store.MarkJobRunningAsync(job.Id, ct);

        var (outcome, links) = await CrawlAsync(job, page, ct);
        await store.SaveResultAsync(job, page, outcome, links, ct);

        logger.LogInformation(
            "Page {Url} at depth {Depth}: {Status}, {LinkCount} links, ratio {Ratio}",
            page.Url, page.Depth, outcome.Status, outcome.OutgoingLinkCount, outcome.DomainLinkRatio);
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

    private static DiscoveredLink Classify(Uri url, string startHost, int childDepth, int maxDepth)
    {
        if (!DomainLinkRatio.IsWithinDomain(url.Host, startHost))
            return new(url.AbsoluteUri, false, CrawlPageStatus.Skipped, "External link");

        if (childDepth > maxDepth)
            return new(url.AbsoluteUri, true, CrawlPageStatus.Skipped, "Beyond max depth");

        // TODO(next step): reserve a page slot, mark Pending and publish a crawl message.
        return new(url.AbsoluteUri, true, CrawlPageStatus.Skipped, "Not followed yet");
    }
}
