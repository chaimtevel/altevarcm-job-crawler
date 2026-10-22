using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveredByPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "discovered_by_page_id",
                table: "crawl_pages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_pages_discovered_by_page_id",
                table: "crawl_pages",
                column: "discovered_by_page_id");

            migrationBuilder.AddForeignKey(
                name: "fk_crawl_pages_crawl_pages_discovered_by_page_id",
                table: "crawl_pages",
                column: "discovered_by_page_id",
                principalTable: "crawl_pages",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_crawl_pages_crawl_pages_discovered_by_page_id",
                table: "crawl_pages");

            migrationBuilder.DropIndex(
                name: "ix_crawl_pages_discovered_by_page_id",
                table: "crawl_pages");

            migrationBuilder.DropColumn(
                name: "discovered_by_page_id",
                table: "crawl_pages");
        }
    }
}
