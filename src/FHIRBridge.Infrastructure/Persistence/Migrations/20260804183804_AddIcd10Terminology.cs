using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIcd10Terminology : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Icd10Codes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OrderNumber = table.Column<int>(type: "int", nullable: false),
                    IsBillable = table.Column<bool>(type: "bit", nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LongDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10Codes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Icd10ImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedCodeCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10ImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10Versions",
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
                    table.PrimaryKey("PK_Icd10Versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Icd10Codes_IsActive_Code",
                schema: "terminology",
                table: "Icd10Codes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Icd10ImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "Icd10ImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10Versions_IsActive",
                schema: "terminology",
                table: "Icd10Versions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10Versions_Version",
                schema: "terminology",
                table: "Icd10Versions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Icd10Codes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10ImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10Versions",
                schema: "terminology");
        }
    }
}
