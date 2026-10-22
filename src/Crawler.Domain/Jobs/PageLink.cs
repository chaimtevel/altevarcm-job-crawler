namespace Crawler.Domain.Jobs;

/// <summary>A parent → child edge: the parent page contains a link to the child page.</summary>
public class PageLink
{
    public Guid JobId { get; set; }
    public long ParentPageId { get; set; }
    public long ChildPageId { get; set; }
}
