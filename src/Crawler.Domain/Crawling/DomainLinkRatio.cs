namespace Crawler.Domain.Crawling;

public static class DomainLinkRatio
{
    /// <summary>
    /// (# links within the starting domain) / (total # links); 0 when there are no links.
    /// Callers pass already-normalized http(s) links.
    /// </summary>
    public static double Calculate(IReadOnlyCollection<Uri> outgoingLinks, string startingHost)
    {
        if (outgoingLinks.Count == 0)
        {
            return 0;
        }

        var internalCount = outgoingLinks.Count(link => IsWithinDomain(link.Host, startingHost));
        return (double)internalCount / outgoingLinks.Count;
    }

    /// <summary>
    /// True when <paramref name="host"/> is the starting host or one of its subdomains.
    /// A leading "www." is ignored on both sides, so www.example.com, example.com and
    /// blog.example.com are all the same domain. Scheme and port are not considered.
    /// </summary>
    public static bool IsWithinDomain(string host, string startingHost)
    {
        var domain = StripWww(startingHost);
        var candidate = StripWww(host);

        return candidate.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
