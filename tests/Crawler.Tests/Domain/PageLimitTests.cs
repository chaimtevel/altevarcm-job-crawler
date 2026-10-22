using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;

namespace Crawler.Tests.Domain;

public class PageLimitTests
{
    private static DiscoveredLink Pending(string url) => new(url, true, CrawlPageStatus.Pending, null);

    private static DiscoveredLink External(string url) => new(url, false, CrawlPageStatus.Skipped, "External link");

    [Fact]
    public void Keeps_pending_links_while_slots_remain()
    {
        var (links, reserved) = PageLimit.Apply([Pending("a"), Pending("b")], new HashSet<string>(), 5);

        Assert.Equal(2, reserved);
        Assert.All(links, l => Assert.Equal(CrawlPageStatus.Pending, l.Status));
    }

    [Fact]
    public void Skips_pending_links_beyond_the_remaining_slots()
    {
        var (links, reserved) = PageLimit.Apply([Pending("a"), Pending("b"), Pending("c")], new HashSet<string>(), 2);

        Assert.Equal(2, reserved);
        Assert.Equal(CrawlPageStatus.Pending, links[1].Status);
        Assert.Equal(CrawlPageStatus.Skipped, links[2].Status);
        Assert.Equal(PageLimit.LimitReachedReason, links[2].StatusReason);
    }

    [Fact]
    public void Known_urls_and_skipped_links_use_no_slots()
    {
        var known = new HashSet<string> { "a" };

        var (links, reserved) = PageLimit.Apply([Pending("a"), External("x"), Pending("b")], known, 1);

        Assert.Equal(1, reserved);
        Assert.Equal(CrawlPageStatus.Pending, links[2].Status);
    }

    [Fact]
    public void Reserves_nothing_when_no_slots_remain()
    {
        var (links, reserved) = PageLimit.Apply([Pending("a")], new HashSet<string>(), 0);

        Assert.Equal(0, reserved);
        Assert.Equal(CrawlPageStatus.Skipped, links[0].Status);
    }
}
