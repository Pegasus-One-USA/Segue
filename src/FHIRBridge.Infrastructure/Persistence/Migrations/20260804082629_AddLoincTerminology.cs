using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoincTerminology : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "terminology");

            migrationBuilder.CreateTable(
                name: "LoincAnswerLists",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerListId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AnswerListName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LoincCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AnswerCode = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AnswerDisplay = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincAnswerLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincConceptMaps",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetSystem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TargetCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Equivalence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Display = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincConceptMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincConcepts",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LongCommonName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Class = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Component = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Property = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimeAspect = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    System = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Scale = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Method = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincConcepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincGroups",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentGroupId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincImportHistory",
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
                    table.PrimaryKey("PK_LoincImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincParts",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PartTypeName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PartName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PartDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincParts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincVersions",
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
                    table.PrimaryKey("PK_LoincVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoincAnswerLists_AnswerListId_AnswerCode_Version",
                schema: "terminology",
                table: "LoincAnswerLists",
                columns: new[] { "AnswerListId", "AnswerCode", "Version" },
                unique: true,
                filter: "[AnswerCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoincConceptMaps_SourceSystem_SourceCode_TargetSystem_Version",
                schema: "terminology",
                table: "LoincConceptMaps",
                columns: new[] { "SourceSystem", "SourceCode", "TargetSystem", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_LoincConcepts_Code",
                schema: "terminology",
                table: "LoincConcepts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincConcepts_IsActive_Code",
                schema: "terminology",
                table: "LoincConcepts",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_LoincGroups_GroupId_Version",
                schema: "terminology",
                table: "LoincGroups",
                columns: new[] { "GroupId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "LoincImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LoincParts_PartNumber_Version",
                schema: "terminology",
                table: "LoincParts",
                columns: new[] { "PartNumber", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincVersions_IsActive",
                schema: "terminology",
                table: "LoincVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LoincVersions_Version",
                schema: "terminology",
                table: "LoincVersions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoincAnswerLists",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincConceptMaps",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincConcepts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincGroups",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincParts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincVersions",
                schema: "terminology");
        }
    }
}
