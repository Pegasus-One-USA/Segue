using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddCdsCoverageRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CoverageAssertionCount",
                table: "CdsCallLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverageDecisions",
                table: "CdsCallLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PayerId",
                table: "CdsCallLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerName",
                table: "CdsCallLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RelayMs",
                table: "CdsCallLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelayOutcome",
                table: "CdsCallLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RelayUsed",
                table: "CdsCallLogs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SystemActionCount",
                table: "CdsCallLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CdsCoverageRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    System = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CodePrefixMatch = table.Column<bool>(type: "boolean", nullable: false),
                    Covered = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PaNeeded = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DocNeeded = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DocPurposes = table.Column<List<string>>(type: "text[]", nullable: false),
                    InfoNeeded = table.Column<List<string>>(type: "text[]", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PolicyUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    QuestionnaireCanonical = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "timezone('utc', now())"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CdsCoverageRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CdsPayers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Identifiers = table.Column<List<string>>(type: "text[]", nullable: false),
                    PlanValues = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RelayEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RelayServiceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RelayTimeoutMs = table.Column<int>(type: "integer", nullable: false),
                    FallbackToLocalRules = table.Column<bool>(type: "boolean", nullable: false),
                    ForwardFhirAuthorization = table.Column<bool>(type: "boolean", nullable: false),
                    RelayClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "timezone('utc', now())"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CdsPayers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CdsCoverageRules_Code",
                table: "CdsCoverageRules",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_CdsCoverageRules_PayerId_System_Code_EffectiveFrom",
                table: "CdsCoverageRules",
                columns: new[] { "PayerId", "System", "Code", "EffectiveFrom" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CdsPayers_Name",
                table: "CdsPayers",
                column: "Name",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CdsCoverageRules");

            migrationBuilder.DropTable(
                name: "CdsPayers");

            migrationBuilder.DropColumn(
                name: "CoverageAssertionCount",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "CoverageDecisions",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "PayerId",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "PayerName",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "RelayMs",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "RelayOutcome",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "RelayUsed",
                table: "CdsCallLogs");

            migrationBuilder.DropColumn(
                name: "SystemActionCount",
                table: "CdsCallLogs");
        }
    }
}
