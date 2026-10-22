using Crawler.Worker.Crawling;

namespace Crawler.Tests.Worker;

public class SitemapReaderTests
{
    [Fact]
    public void Robots_sitemap_lines_are_read_case_insensitively()
    {
        const string robots = """
            User-agent: *
            Disallow: /wp-admin/
            Sitemap: https://example.com/wp-sitemap.xml
            sitemap:   https://example.com/news.xml
            Sitemap: not-a-url
            """;

        Assert.Equal(
            ["https://example.com/wp-sitemap.xml", "https://example.com/news.xml"],
            SitemapReader.ParseRobots(robots).Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void Urlset_yields_page_urls()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/job/corporate-recruiter/</loc><lastmod>2026-01-01</lastmod></url>
              <url><loc> https://example.com/job/nurse/ </loc></url>
              <url><loc>ftp://example.com/file</loc></url>
            </urlset>
            """;

        var (pages, sitemaps) = SitemapReader.Parse(xml);

        Assert.Equal(["https://example.com/job/corporate-recruiter/", "https://example.com/job/nurse/"], pages.Select(u => u.AbsoluteUri));
        Assert.Empty(sitemaps);
    }

    [Fact]
    public void Sitemap_index_yields_nested_sitemaps()
    {
        const string xml = """
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://example.com/wp-sitemap-posts-job-1.xml</loc></sitemap>
            </sitemapindex>
            """;

        var (pages, sitemaps) = SitemapReader.Parse(xml);

        Assert.Empty(pages);
        Assert.Equal(["https://example.com/wp-sitemap-posts-job-1.xml"], sitemaps.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("<html><body>Not found</body>")]
    [InlineData("""<!DOCTYPE urlset [<!ENTITY x "boom">]><urlset><url><loc>&x;</loc></url></urlset>""")]
    public void Invalid_xml_and_dtds_yield_nothing(string xml)
    {
        var (pages, sitemaps) = SitemapReader.Parse(xml);

        Assert.Empty(pages);
        Assert.Empty(sitemaps);
    }
}
