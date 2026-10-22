using Crawler.Infrastructure.Messaging;

namespace Crawler.Tests.Infrastructure;

public class CrawlMessagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ttl_is_the_time_remaining_until_the_job_deadline()
    {
        var metadata = CrawlMessaging.PublishMetadata(Now.AddMinutes(15), Now);

        Assert.Equal("900", metadata["ttlInSeconds"]);
    }

    [Fact]
    public void Ttl_rounds_partial_seconds_up()
    {
        var metadata = CrawlMessaging.PublishMetadata(Now.AddSeconds(10.2), Now);

        Assert.Equal("11", metadata["ttlInSeconds"]);
    }

    [Fact]
    public void Ttl_is_at_least_one_second_when_deadline_has_passed()
    {
        var metadata = CrawlMessaging.PublishMetadata(Now.AddMinutes(-1), Now);

        Assert.Equal("1", metadata["ttlInSeconds"]);
    }
}
