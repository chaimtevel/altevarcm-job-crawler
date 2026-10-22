using Crawler.Api.Jobs;
using Crawler.Domain.Jobs;

namespace Crawler.Tests.Api;

public class JobTreeBuilderTests
{
    private static CrawlPage Page(long id, long? parentId, int depth) => new()
    {
        Id = id,
        Url = $"https://example.com/{id}",
        DiscoveredByPageId = parentId,
        Depth = depth,
        Status = CrawlPageStatus.Crawled,
    };

    [Fact]
    public void Returns_null_when_there_is_no_root()
    {
        Assert.Null(JobTreeBuilder.Build([]));
    }

    [Fact]
    public void Nests_pages_under_the_page_that_discovered_them()
    {
        var pages = new[] { Page(3, 1, 1), Page(1, null, 0), Page(2, 1, 1), Page(4, 2, 2) };

        var root = JobTreeBuilder.Build(pages)!;

        Assert.Equal(1, root.PageId);
        Assert.Equal([2L, 3L], root.Children.Select(c => c.PageId));
        Assert.Equal(4, root.Children[0].Children.Single().PageId);
        Assert.Empty(root.Children[1].Children);
    }
}
