using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRxNormTerminology : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RxNormConcepts",
                schema: "terminology",
                columns: table => new
                {
                    Rxcui = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: false),
                    TermType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RxNormConcepts", x => x.Rxcui);
                });

            migrationBuilder.CreateTable(
                name: "RxNormImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RxNormImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RxNormVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RxNormVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RxNormConcepts_IsActive_Rxcui",
                schema: "terminology",
                table: "RxNormConcepts",
                columns: new[] { "IsActive", "Rxcui" });

            migrationBuilder.CreateIndex(
                name: "IX_RxNormImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "RxNormImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RxNormVersions_IsActive",
                schema: "terminology",
                table: "RxNormVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RxNormVersions_Version",
                schema: "terminology",
                table: "RxNormVersions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RxNormConcepts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "RxNormImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "RxNormVersions",
                schema: "terminology");
        }
    }
}
