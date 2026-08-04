using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnomedTerminology : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SnomedConcepts",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    EffectiveTime = table.Column<DateOnly>(type: "date", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    ModuleId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    DefinitionStatusId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Fsn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PreferredTerm = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedConcepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedDescriptions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    ConceptId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Term = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TypeId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    CaseSignificanceId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedDescriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedImportHistory",
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
                    table.PrimaryKey("PK_SnomedImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedRelationships",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    DestinationId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    TypeId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    RelationshipGroup = table.Column<int>(type: "int", nullable: false),
                    CharacteristicTypeId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedRelationships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedVersions",
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
                    table.PrimaryKey("PK_SnomedVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnomedConcepts_Active_Id",
                schema: "terminology",
                table: "SnomedConcepts",
                columns: new[] { "Active", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SnomedDescriptions_ConceptId",
                schema: "terminology",
                table: "SnomedDescriptions",
                column: "ConceptId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "SnomedImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedRelationships_DestinationId",
                schema: "terminology",
                table: "SnomedRelationships",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedRelationships_SourceId",
                schema: "terminology",
                table: "SnomedRelationships",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedVersions_IsActive",
                schema: "terminology",
                table: "SnomedVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedVersions_Version",
                schema: "terminology",
                table: "SnomedVersions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SnomedConcepts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedDescriptions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedRelationships",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedVersions",
                schema: "terminology");
        }
    }
}
