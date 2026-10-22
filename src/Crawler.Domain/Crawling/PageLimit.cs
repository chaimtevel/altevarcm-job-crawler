using Crawler.Domain.Jobs;

namespace Crawler.Domain.Crawling;

public static class PageLimit
{
    public const string LimitReachedReason = SkipReasons.PageLimitReached;

    /// <summary>
    /// Keeps new Pending links while slots remain; the rest become Skipped. Already-known URLs use no slot.
    /// Returns the adjusted links and how many slots were reserved.
    /// </summary>
    public static (IReadOnlyList<DiscoveredLink> Links, int Reserved) Apply(
        IReadOnlyList<DiscoveredLink> links, IReadOnlySet<string> knownUrls, int remainingSlots)
    {
        var reserved = 0;
        var result = new List<DiscoveredLink>(links.Count);

        foreach (var link in links)
        {
            if (link.Status != CrawlPageStatus.Pending || knownUrls.Contains(link.Url))
            {
                result.Add(link);
            }
            else if (reserved < remainingSlots)
            {
                reserved++;
                result.Add(link);
            }
            else
            {
                result.Add(link with { Status = CrawlPageStatus.Skipped, StatusReason = LimitReachedReason });
            }
        }

        return (result, reserved);
    }
}
