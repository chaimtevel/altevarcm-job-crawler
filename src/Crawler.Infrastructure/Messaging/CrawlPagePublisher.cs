using Crawler.Domain.Jobs;
using Dapr.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>Publishes crawl messages through Dapr; each message expires at the job's deadline.</summary>
public class CrawlPagePublisher(DaprClient dapr, TimeProvider clock)
{
    public Task PublishAsync(CrawlJob job, IEnumerable<QueuedPage> pages, string correlationId, CancellationToken ct)
    {
        var metadata = CrawlMessaging.PublishMetadata(job.GetDeadline(), clock.GetUtcNow());

        return Task.WhenAll(pages.Select(page => dapr.PublishEventAsync(
            CrawlMessaging.PubSubName,
            CrawlMessaging.CrawlPageTopic,
            new CrawlPageMessage(job.Id, page.Id, page.Url, page.Depth, correlationId),
            metadata,
            ct)));
    }
}
