using Crawler.Domain.Crawling;

namespace Crawler.Tests.Domain;

public class UrlNormalizerTests
{
    private static readonly Uri PageUrl = new("https://example.com/blog/post");

    [Theory]
    [InlineData("/about", "https://example.com/about")]
    [InlineData("contact", "https://example.com/blog/contact")]
    [InlineData("../pricing", "https://example.com/pricing")]
    [InlineData("./archive/2024", "https://example.com/blog/archive/2024")]
    [InlineData("//cdn.example.org/lib", "https://cdn.example.org/lib")]
    [InlineData("?page=2", "https://example.com/blog/post?page=2")]
    public void Resolves_relative_links_against_the_page_url(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(href, PageUrl, out var result));
        Assert.Equal(expected, result.AbsoluteUri);
    }

    [Theory]
    [InlineData("#section", "https://example.com/blog/post")]
    [InlineData("/about#team", "https://example.com/about")]
    [InlineData("https://other.com/page?q=1#top", "https://other.com/page?q=1")]
    public void Removes_fragments(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(href, PageUrl, out var result));
        Assert.Equal(expected, result.AbsoluteUri);
    }

    [Theory]
    [InlineData("HTTPS://Example.COM/Path", "https://example.com/Path")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("https://user:pass@example.com/a", "https://example.com/a")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("  /about  ", "https://example.com/about")]
    public void Canonicalizes_scheme_host_port_and_whitespace(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(href, PageUrl, out var result));
        Assert.Equal(expected, result.AbsoluteUri);
    }

    [Fact]
    public void Equivalent_links_normalize_to_the_same_url()
    {
        UrlNormalizer.TryNormalize("/about#team", PageUrl, out var a);
        UrlNormalizer.TryNormalize("HTTPS://EXAMPLE.com:443/about", PageUrl, out var b);
        UrlNormalizer.TryNormalize("../about", PageUrl, out var c);

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+15551234567")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://example.com/file.txt")]
    [InlineData("data:text/plain,hello")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_empty_and_non_http_links(string? href)
    {
        Assert.False(UrlNormalizer.TryNormalize(href, PageUrl, out _));
    }

    [Fact]
    public void Rejects_urls_longer_than_the_max_length()
    {
        var tooLong = "https://example.com/?q=" + new string('a', UrlNormalizer.MaxLength);

        Assert.False(UrlNormalizer.TryNormalize(tooLong, PageUrl, out _));
    }
}
