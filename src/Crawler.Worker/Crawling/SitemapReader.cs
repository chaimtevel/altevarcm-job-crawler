using System.Xml;
using System.Xml.Linq;

namespace Crawler.Worker.Crawling;

/// <summary>
/// Finds a site's page URLs from its sitemaps: robots.txt "Sitemap:" lines, else /sitemap.xml.
/// Catches pages a crawler can't reach through links, e.g. job lists rendered by JavaScript.
/// </summary>
public class SitemapReader(PageFetcher fetcher, ILogger<SitemapReader> logger)
{
    private const int MaxSitemapFiles = 20;
    private const int MaxUrls = 5000;

    /// <summary>Keeps the start page within the worker's claim lease even on slow sites.</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(45);

    /// <summary>Page URLs listed in the site's sitemaps; empty when there are none or they can't be read.</summary>
    public async Task<IReadOnlyList<Uri>> ReadAsync(Uri siteUrl, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TotalBudget);

        var origin = new Uri(siteUrl.GetLeftPart(UriPartial.Authority));
        var pages = new List<Uri>();
        var seen = new HashSet<Uri>();

        try
        {
            var robots = await fetcher.FetchTextAsync(new Uri(origin, "/robots.txt"), budget.Token);
            var listed = robots is null ? [] : ParseRobots(robots);
            var queue = new Queue<Uri>(listed.Count > 0 ? listed : [new Uri(origin, "/sitemap.xml")]);

            while (queue.Count > 0 && seen.Count < MaxSitemapFiles && pages.Count < MaxUrls)
            {
                var sitemap = queue.Dequeue();
                if (!seen.Add(sitemap))
                {
                    continue;
                }

                var xml = await fetcher.FetchTextAsync(sitemap, budget.Token);
                if (xml is null)
                {
                    continue;
                }

                var (pageUrls, childSitemaps) = Parse(xml);
                pages.AddRange(pageUrls.Take(MaxUrls - pages.Count));
                foreach (var child in childSitemaps)
                {
                    queue.Enqueue(child);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Sitemap reading for {Site} hit its {Budget}s budget; using {Count} URLs found so far",
                origin, TotalBudget.TotalSeconds, pages.Count);
        }

        logger.LogInformation("Read {Files} sitemap files for {Site}: {Count} page URLs", seen.Count, origin, pages.Count);
        return pages;
    }

    /// <summary>Absolute http(s) URLs from robots.txt "Sitemap:" lines.</summary>
    public static IReadOnlyList<Uri> ParseRobots(string robotsTxt) =>
        robotsTxt.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("sitemap:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["sitemap:".Length..].Trim())
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var url) ? url : null)
            .OfType<Uri>()
            .Where(url => url.Scheme is "http" or "https")
            .ToList();

    /// <summary>
    /// Splits a sitemap into page URLs (&lt;urlset&gt;) and nested sitemaps (&lt;sitemapindex&gt;).
    /// Invalid XML yields nothing; DTDs are rejected, which blocks XML entity attacks.
    /// </summary>
    public static (IReadOnlyList<Uri> Pages, IReadOnlyList<Uri> Sitemaps) Parse(string xml)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return ([], []);
        }

        var root = document.Root;
        var locs = root?.Elements()
            .Select(entry => entry.Elements().FirstOrDefault(e => e.Name.LocalName == "loc")?.Value.Trim())
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var url) ? url : null)
            .OfType<Uri>()
            .Where(url => url.Scheme is "http" or "https")
            .ToList() ?? [];

        return root?.Name.LocalName == "sitemapindex" ? ([], locs) : (locs, []);
    }
}
