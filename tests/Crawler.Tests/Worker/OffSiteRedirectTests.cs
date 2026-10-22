using Crawler.Domain.Jobs;
using Crawler.Worker.Crawling;

namespace Crawler.Tests.Worker;

public class OffSiteRedirectTests
{
    private const string StartHost = "example.com";

    [Fact]
    public void Link_redirecting_to_another_site_is_skipped_without_ratio_or_links()
    {
        var outcome = CrawlPageHandler.OffSiteRedirect(1, new Uri("https://auth.other.org/login"), StartHost, 200);

        Assert.NotNull(outcome);
        Assert.Equal(CrawlPageStatus.Skipped, outcome.Status);
        Assert.Null(outcome.DomainLinkRatio);
        Assert.Null(outcome.OutgoingLinkCount);
        Assert.Equal("Redirects off-site to https://auth.other.org/login", outcome.StatusReason);
    }

    [Theory]
    [InlineData("https://example.com/about")]
    [InlineData("https://www.example.com/about")]
    [InlineData("https://blog.example.com/post")]
    public void Redirect_within_the_site_is_crawled(string finalUrl)
    {
        Assert.Null(CrawlPageHandler.OffSiteRedirect(1, new Uri(finalUrl), StartHost, 200));
    }

    [Theory]
    [InlineData("https://example.org/")]      // domain move
    [InlineData("https://parent.example/")]   // subdomain start redirecting to its parent
    public void Start_page_is_crawled_wherever_it_lands(string finalUrl)
    {
        Assert.Null(CrawlPageHandler.OffSiteRedirect(0, new Uri(finalUrl), StartHost, 200));
    }
}
