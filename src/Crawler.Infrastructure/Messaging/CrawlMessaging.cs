namespace Crawler.Infrastructure.Messaging;

public static class CrawlMessaging
{
    /// <summary>Dapr pub/sub component name (dapr/components/pubsub.yaml).</summary>
    public const string PubSubName = "crawl-pubsub";

    public const string CrawlPageTopic = "crawl-page";

    /// <summary>Dapr publish metadata: the message expires at the job's deadline.</summary>
    public static Dictionary<string, string> PublishMetadata(DateTimeOffset jobDeadline, DateTimeOffset now)
    {
        var ttlSeconds = Math.Max(1, (int)Math.Ceiling((jobDeadline - now).TotalSeconds));
        return new() { ["ttlInSeconds"] = ttlSeconds.ToString() };
    }
}

/// <summary>One message per page to crawl. The page row already exists with status Pending.</summary>
public record CrawlPageMessage(Guid JobId, long PageId, string Url, int Depth, string CorrelationId);
