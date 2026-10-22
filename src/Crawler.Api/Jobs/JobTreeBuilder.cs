using Crawler.Domain.Jobs;

namespace Crawler.Api.Jobs;

public record JobTreeNode(
    long PageId,
    string Url,
    int Depth,
    bool IsInternal,
    CrawlPageStatus Status,
    string? StatusReason,
    int? HttpStatusCode,
    double? DomainLinkRatio,
    int? OutgoingLinkCount,
    IReadOnlyList<JobTreeNode> Children);

public static class JobTreeBuilder
{
    /// <summary>Builds the tree from each page's first-discovering parent; null if there is no root page.</summary>
    public static JobTreeNode? Build(IReadOnlyList<CrawlPage> pages)
    {
        var root = pages.FirstOrDefault(p => p.Depth == 0 && p.DiscoveredByPageId is null);
        if (root is null)
        {
            return null;
        }

        var childrenByParent = pages
            .Where(p => p.DiscoveredByPageId is not null)
            .OrderBy(p => p.Id)
            .ToLookup(p => p.DiscoveredByPageId!.Value);

        return ToNode(root, childrenByParent);
    }

    private static JobTreeNode ToNode(CrawlPage page, ILookup<long, CrawlPage> childrenByParent) => new(
        page.Id,
        page.Url,
        page.Depth,
        page.IsInternal,
        page.Status,
        page.StatusReason,
        page.HttpStatusCode,
        page.DomainLinkRatio,
        page.OutgoingLinkCount,
        childrenByParent[page.Id].Select(child => ToNode(child, childrenByParent)).ToList());
}
