using Crawler.Domain.Crawling;
using Crawler.Worker.Crawling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Crawler.Tests.Integration;

/// <summary>
/// Serves local HTML fixtures over HTTP (in-memory TestServer) and runs the real
/// fetch → link extraction → normalization → Domain Link Ratio pipeline against them.
/// </summary>
public sealed class FixtureSiteCrawlTests : IAsyncLifetime
{
    private const string StartHost = "localhost";
    private IHost _site = null!;
    private PageFetcher _fetcher = null!;

    public async Task InitializeAsync()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "site");
        _site = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    // TestServer's client doesn't follow redirects, like a refused https→http downgrade.
                    app.Map("/moved", moved => moved.Run(ctx => Redirect(ctx, "blog/post.html")));
                    app.Map("/loop", loop => loop.Run(ctx => Redirect(ctx, "/loop")));
                    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(fixtures) });
                }))
            .StartAsync();

        _fetcher = new PageFetcher(_site.GetTestClient());
    }

    public async Task DisposeAsync()
    {
        await _site.StopAsync();
        _site.Dispose();
    }

    [Fact]
    public async Task Home_page_links_are_normalized_deduplicated_and_scored()
    {
        var (links, ratio) = await CrawlAsync("http://localhost/index.html");

        Assert.Equal(
            [
                "http://localhost/about.html",        // "/about.html" and "about.html#team" collapse to one
                "http://localhost/blog/post.html",    // "./blog/post.html"
                "https://external.example.org/page",
                "http://localhost/index.html",        // "#top" points back to the page itself
            ],
            links);
        // mailto:, tel: and javascript: are ignored; 3 of 4 links are internal.
        Assert.Equal(0.75, ratio);
    }

    [Fact]
    public async Task Relative_links_resolve_against_base_href()
    {
        var (links, ratio) = await CrawlAsync("http://localhost/blog/post.html");

        Assert.Equal(["http://localhost/index.html", "http://localhost/docs/guide.html"], links);
        Assert.Equal(1, ratio);
    }

    [Fact]
    public async Task Page_without_links_has_ratio_zero()
    {
        var (links, ratio) = await CrawlAsync("http://localhost/about.html");

        Assert.Empty(links);
        Assert.Equal(0, ratio);
    }

    [Fact]
    public async Task Non_html_content_is_not_crawled()
    {
        var result = await _fetcher.FetchAsync(new Uri("http://localhost/data.txt"), CancellationToken.None);

        Assert.Equal(FetchStatus.NotHtml, result.Status);
    }

    [Fact]
    public async Task Missing_page_is_reported_as_failed_with_status_code()
    {
        var result = await _fetcher.FetchAsync(new Uri("http://localhost/missing.html"), CancellationToken.None);

        Assert.Equal(FetchStatus.Failed, result.Status);
        Assert.Equal(404, result.HttpStatusCode);
    }

    [Fact]
    public async Task Redirect_is_followed_and_links_resolve_against_the_target()
    {
        var result = await _fetcher.FetchAsync(new Uri("http://localhost/moved"), CancellationToken.None);

        Assert.Equal(FetchStatus.Html, result.Status);
        Assert.Equal("http://localhost/blog/post.html", result.FinalUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task Redirect_loop_fails_after_the_hop_limit()
    {
        var result = await _fetcher.FetchAsync(new Uri("http://localhost/loop"), CancellationToken.None);

        Assert.Equal(FetchStatus.Failed, result.Status);
        Assert.Equal("Too many redirects", result.Reason);
    }

    private static Task Redirect(HttpContext context, string location)
    {
        context.Response.Redirect(location, permanent: true);
        return Task.CompletedTask;
    }

    private async Task<(string[] Links, double Ratio)> CrawlAsync(string url)
    {
        var result = await _fetcher.FetchAsync(new Uri(url), CancellationToken.None);
        Assert.Equal(FetchStatus.Html, result.Status);

        var links = HtmlLinkExtractor.ExtractLinks(result.Html!, result.FinalUrl!);
        return (links.Select(l => l.AbsoluteUri).ToArray(), DomainLinkRatio.Calculate(links, StartHost));
    }
}
