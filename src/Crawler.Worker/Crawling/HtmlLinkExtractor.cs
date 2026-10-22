using AngleSharp.Html.Parser;
using Crawler.Domain.Crawling;

namespace Crawler.Worker.Crawling;

public static class HtmlLinkExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Returns the distinct, normalized http(s) links of a page, honouring &lt;base href&gt;.</summary>
    public static IReadOnlyList<Uri> ExtractLinks(string html, Uri pageUrl)
    {
        using var document = Parser.ParseDocument(html);

        var baseHref = document.QuerySelector("base[href]")?.GetAttribute("href");
        var baseUrl = baseHref is not null && Uri.TryCreate(pageUrl, baseHref, out var resolved) ? resolved : pageUrl;

        return document.QuerySelectorAll("a[href], area[href]")
            .Select(element => element.GetAttribute("href"))
            .Select(href => UrlNormalizer.TryNormalize(href, baseUrl, out var url) ? url : null)
            .OfType<Uri>()
            .Distinct()
            .ToList();
    }
}
