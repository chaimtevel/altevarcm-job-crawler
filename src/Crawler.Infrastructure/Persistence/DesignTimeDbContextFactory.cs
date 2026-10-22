using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crawler.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef migrations add`, which never connects to the database,
/// so no real connection string or credentials are needed here.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CrawlerDbContext>
{
    public CrawlerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CrawlerDbContext>();
        PersistenceRegistration.Configure(options, "Host=localhost;Database=crawler");
        return new CrawlerDbContext(options.Options);
    }
}
