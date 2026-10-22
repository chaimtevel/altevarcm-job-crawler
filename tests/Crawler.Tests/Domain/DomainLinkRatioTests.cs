using Crawler.Domain.Crawling;

namespace Crawler.Tests.Domain;

public class DomainLinkRatioTests
{
    private const string StartingHost = "example.com";

    private static Uri[] Links(params string[] urls) => urls.Select(u => new Uri(u)).ToArray();

    [Fact]
    public void Returns_zero_when_page_has_no_links()
    {
        Assert.Equal(0, DomainLinkRatio.Calculate([], StartingHost));
    }

    [Fact]
    public void Returns_one_when_all_links_are_internal()
    {
        var links = Links("https://example.com/a", "https://example.com/b");

        Assert.Equal(1, DomainLinkRatio.Calculate(links, StartingHost));
    }

    [Fact]
    public void Returns_zero_when_all_links_are_external()
    {
        var links = Links("https://other.com/a", "https://another.org/b");

        Assert.Equal(0, DomainLinkRatio.Calculate(links, StartingHost));
    }

    [Fact]
    public void Returns_fraction_of_internal_links()
    {
        var links = Links(
            "https://example.com/a",
            "https://other.com/b",
            "https://other.com/c",
            "https://another.org/d");

        Assert.Equal(0.25, DomainLinkRatio.Calculate(links, StartingHost));
    }

    [Fact]
    public void Host_match_is_case_insensitive()
    {
        var links = Links("https://example.com/a");

        Assert.Equal(1, DomainLinkRatio.Calculate(links, "EXAMPLE.COM"));
    }

    [Fact]
    public void Ignores_scheme_and_port_when_matching_host()
    {
        var links = Links("http://example.com/a", "https://example.com:8443/b");

        Assert.Equal(1, DomainLinkRatio.Calculate(links, StartingHost));
    }

    [Fact]
    public void Treats_subdomains_as_internal()
    {
        var links = Links("https://blog.example.com/a", "https://a.b.example.com/b");

        Assert.Equal(1, DomainLinkRatio.Calculate(links, StartingHost));
    }

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com", "www.example.com")]
    [InlineData("www.example.com", "blog.example.com")]
    public void Ignores_leading_www(string startingHost, string linkHost)
    {
        Assert.True(DomainLinkRatio.IsWithinDomain(linkHost, startingHost));
    }

    [Theory]
    [InlineData("notexample.com")]
    [InlineData("example.com.evil.org")]
    [InlineData("example.org")]
    public void Lookalike_hosts_are_external(string linkHost)
    {
        Assert.False(DomainLinkRatio.IsWithinDomain(linkHost, StartingHost));
    }
}
