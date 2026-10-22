using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveJobsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_crawl_jobs_active_created_at",
                table: "crawl_jobs",
                column: "created_at",
                filter: "status IN ('Pending', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_crawl_jobs_active_created_at",
                table: "crawl_jobs");
        }
    }
}
