using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddHapiLocalTerminologyAndImportHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorReferenceId",
                table: "WorkflowRuns",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorReferenceId",
                table: "PipelineRunRouteExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HapiTerminologyImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeSystem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HapiTerminologyImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CODESYSTEM",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CodeSystemUri = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CsName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CurrentVersionPid = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CODESYSTEM", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CODESYSTEM_VER",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CodeSystemPid = table.Column<long>(type: "bigint", nullable: false),
                    CsVersionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CsDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CODESYSTEM_VER", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CONCEPT",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CodeSystemPid = table.Column<long>(type: "bigint", nullable: false),
                    CodeVal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Display = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CONCEPT", x => x.Pid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HapiTerminologyImportHistory_CodeSystem_StartedOnUtc",
                schema: "terminology",
                table: "HapiTerminologyImportHistory",
                columns: new[] { "CodeSystem", "StartedOnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CODESYSTEM_CodeSystemUri",
                schema: "terminology",
                table: "TRM_CODESYSTEM",
                column: "CodeSystemUri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CODESYSTEM_VER_CodeSystemPid_CsVersionId",
                schema: "terminology",
                table: "TRM_CODESYSTEM_VER",
                columns: new[] { "CodeSystemPid", "CsVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CONCEPT_CodeSystemPid_CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                columns: new[] { "CodeSystemPid", "CodeVal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CONCEPT_CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                column: "CodeVal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HapiTerminologyImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CODESYSTEM",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CODESYSTEM_VER",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CONCEPT",
                schema: "terminology");

            migrationBuilder.DropColumn(
                name: "ErrorReferenceId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ErrorReferenceId",
                table: "PipelineRunRouteExecutions");
        }
    }
}
