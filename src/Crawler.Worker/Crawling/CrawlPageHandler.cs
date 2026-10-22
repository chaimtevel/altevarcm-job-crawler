using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;

namespace Crawler.Worker.Crawling;

/// <summary>Crawls one page per message. Safe to run twice for the same message.</summary>
public class CrawlPageHandler(
    CrawlStore store,
    PageFetcher fetcher,
    SitemapReader sitemapReader,
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

        if (!job.Status.IsActive())
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

            var result = await CrawlAsync(job, page, ct);
            var children = await store.SaveResultAsync(job, page, result, ct);
            if (children is null)
            {
                logger.LogInformation("Job finished while page {Url} was fetched; result discarded", page.Url);
                return [];
            }

            var outcome = result.Outcome;
            logger.LogInformation(
                "Page {Url} at depth {Depth}: {Status}, {LinkCount} links, {SitemapCount} sitemap URLs, ratio {Ratio}",
                page.Url, page.Depth, outcome.Status, outcome.OutgoingLinkCount, result.SitemapLinks.Count, outcome.DomainLinkRatio);
            return children;
        }
        catch
        {
            // Let the message retry crawl the page instead of waiting for the claim to expire.
            await store.ReleaseClaimAsync(page.Id, CancellationToken.None);
            throw;
        }
    }

    private async Task<PageResult> CrawlAsync(CrawlJob job, CrawlPage page, CancellationToken ct)
    {
        var fetch = await fetcher.FetchAsync(new Uri(page.Url), ct);

        if (fetch.Status != FetchStatus.Html)
        {
            var status = fetch.Status == FetchStatus.NotHtml ? CrawlPageStatus.Skipped : CrawlPageStatus.Failed;
            return PageResult.Without(new PageOutcome(status, fetch.HttpStatusCode, null, null, fetch.Reason));
        }

        var startHost = new Uri(job.Url).Host;
        if (OffSiteRedirect(page.Depth, fetch.FinalUrl!, startHost, fetch.HttpStatusCode) is { } offSite)
        {
            return PageResult.Without(offSite);
        }

        var urls = HtmlLinkExtractor.ExtractLinks(fetch.Html!, fetch.FinalUrl!);
        var ratio = DomainLinkRatio.Calculate(urls, startHost);
        var links = urls.Select(url => Classify(url, startHost, page.Depth + 1, job.MaxDepth)).ToList();
        var outcome = new PageOutcome(CrawlPageStatus.Crawled, fetch.HttpStatusCode, ratio, urls.Count, null);

        // Sitemaps are read once per job, from the start page, and queued alongside its links.
        var sitemapLinks = page.Depth == 0
            ? await ReadSitemapLinksAsync(job, page, fetch.FinalUrl!, links, startHost, ct)
            : [];

        return new PageResult(outcome, links, sitemapLinks, RedirectTarget(page, fetch.FinalUrl!, startHost));
    }

    /// <summary>
    /// Same-site sitemap URLs not already linked from the start page, at depth 1 like its links.
    /// Only crawlable ones are kept, so a huge sitemap can't flood the job with Skipped rows.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredLink>> ReadSitemapLinksAsync(
        CrawlJob job, CrawlPage page, Uri finalUrl, IReadOnlyList<DiscoveredLink> links, string startHost, CancellationToken ct)
    {
        var known = links.Select(l => l.Url).Append(page.Url).Append(finalUrl.AbsoluteUri).ToHashSet();
        var sitemapUrls = await sitemapReader.ReadAsync(finalUrl, ct);

        return sitemapUrls
            .Select(url => UrlNormalizer.TryNormalize(url.AbsoluteUri, url, out var normalized) ? normalized : null)
            .OfType<Uri>()
            .Where(url => DomainLinkRatio.IsWithinDomain(url.Host, startHost))
            .Select(url => Classify(url, startHost, page.Depth + 1, job.MaxDepth))
            .Where(link => link.Status == CrawlPageStatus.Pending && known.Add(link.Url))
            // At most half the page limit, so a large sitemap can't crowd out deeper link-following.
            .Take(job.MaxPages / 2)
            .ToList();
    }

    /// <summary>
    /// A link that redirects off-site (e.g. /login → an auth service) is Skipped with no ratio or links.
    /// The start page is crawled wherever it lands; the starting domain stays the job URL's host.
    /// </summary>
    public static PageOutcome? OffSiteRedirect(int depth, Uri finalUrl, string startHost, int? httpStatusCode)
    {
        if (depth == 0 || DomainLinkRatio.IsWithinDomain(finalUrl.Host, startHost))
        {
            return null;
        }

        return new PageOutcome(CrawlPageStatus.Skipped, httpStatusCode, null, null, $"Redirects off-site to {finalUrl}");
    }

    /// <summary>After a redirect, the final URL is marked as this page so links to it aren't crawled again.</summary>
    private static DiscoveredLink? RedirectTarget(CrawlPage page, Uri finalUrl, string startHost)
    {
        if (!UrlNormalizer.TryNormalize(finalUrl.AbsoluteUri, finalUrl, out var target) || target.AbsoluteUri == page.Url)
        {
            return null;
        }

        var isInternal = DomainLinkRatio.IsWithinDomain(target.Host, startHost);
        return new(target.AbsoluteUri, isInternal, CrawlPageStatus.Skipped, SkipReasons.SamePageAs(page.Url));
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
