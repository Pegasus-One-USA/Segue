using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchiveManifestEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataClass = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ArchivedThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FileLocation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveManifestEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveManifestEntries_CreatedOnUtc",
                table: "ArchiveManifestEntries",
                column: "CreatedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveManifestEntries_DataClass",
                table: "ArchiveManifestEntries",
                column: "DataClass");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveManifestEntries");
        }
    }
}
