using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Crawler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crawl_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    max_depth = table.Column<int>(type: "integer", nullable: false),
                    max_pages = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reserved_page_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crawl_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "crawl_pages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    domain_link_ratio = table.Column<double>(type: "double precision", nullable: true),
                    outgoing_link_count = table.Column<int>(type: "integer", nullable: true),
                    discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    crawled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crawl_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_crawl_pages_crawl_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "crawl_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_links",
                columns: table => new
                {
                    parent_page_id = table.Column<long>(type: "bigint", nullable: false),
                    child_page_id = table.Column<long>(type: "bigint", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_links", x => new { x.parent_page_id, x.child_page_id });
                    table.ForeignKey(
                        name: "fk_page_links_crawl_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "crawl_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_page_links_crawl_pages_child_page_id",
                        column: x => x.child_page_id,
                        principalTable: "crawl_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_page_links_crawl_pages_parent_page_id",
                        column: x => x.parent_page_id,
                        principalTable: "crawl_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_jobs_created_at",
                table: "crawl_jobs",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_pages_job_id_status",
                table: "crawl_pages",
                columns: new[] { "job_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_pages_job_id_url",
                table: "crawl_pages",
                columns: new[] { "job_id", "url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_page_links_child_page_id",
                table: "page_links",
                column: "child_page_id");

            migrationBuilder.CreateIndex(
                name: "ix_page_links_job_id",
                table: "page_links",
                column: "job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_links");

            migrationBuilder.DropTable(
                name: "crawl_pages");

            migrationBuilder.DropTable(
                name: "crawl_jobs");
        }
    }
}
