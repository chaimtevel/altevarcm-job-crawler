using Crawler.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Persistence;

public class CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : DbContext(options)
{
    public DbSet<CrawlJob> Jobs => Set<CrawlJob>();
    public DbSet<CrawlPage> Pages => Set<CrawlPage>();
    public DbSet<PageLink> Links => Set<PageLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlJob>(job =>
        {
            job.ToTable("crawl_jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.Url).IsRequired();
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
            job.Property(j => j.FailureReason).HasMaxLength(2000);

            // History screen: most recent first, paginated.
            job.HasIndex(j => j.CreatedAt).IsDescending();
        });

        modelBuilder.Entity<CrawlPage>(page =>
        {
            page.ToTable("crawl_pages");
            page.HasKey(p => p.Id);
            page.Property(p => p.Id).UseIdentityAlwaysColumn();
            page.Property(p => p.Url).IsRequired();
            page.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            page.Property(p => p.StatusReason).HasMaxLength(2000);

            // Idempotency + de-duplication: a normalized URL exists at most once per job.
            page.HasIndex(p => new { p.JobId, p.Url }).IsUnique();
            // Progress counts and "any pages still pending?" completion check.
            page.HasIndex(p => new { p.JobId, p.Status });
            // Finding a page's discovered children (publishing, tree building).
            page.HasIndex(p => p.DiscoveredByPageId);

            page.HasOne<CrawlJob>().WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Cascade);
            page.HasOne<CrawlPage>().WithMany().HasForeignKey(p => p.DiscoveredByPageId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<PageLink>(link =>
        {
            link.ToTable("page_links");
            // Idempotency: the same parent → child edge can only be stored once.
            link.HasKey(l => new { l.ParentPageId, l.ChildPageId });
            // Loading a job's whole tree in one query.
            link.HasIndex(l => l.JobId);
            link.HasIndex(l => l.ChildPageId);

            link.HasOne<CrawlJob>().WithMany().HasForeignKey(l => l.JobId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<CrawlPage>().WithMany().HasForeignKey(l => l.ParentPageId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<CrawlPage>().WithMany().HasForeignKey(l => l.ChildPageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
