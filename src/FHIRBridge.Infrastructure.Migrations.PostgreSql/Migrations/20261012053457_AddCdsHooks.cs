using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddCdsHooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CdsCallLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ServiceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ServiceInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    HandlerKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Hook = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HookInstance = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TrustedIssuerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: false),
                    CardCount = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CdsCallLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CdsServiceInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HandlerKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Hook = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    CardSourceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CardSourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AllowedIssuerIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
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
                    table.PrimaryKey("PK_CdsServiceInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CdsTrustedIssuers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Vendor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    JwksUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    JwksJson = table.Column<string>(type: "text", nullable: true),
                    Audience = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_CdsTrustedIssuers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CdsCallLogs_OccurredOnUtc",
                table: "CdsCallLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CdsCallLogs_Outcome",
                table: "CdsCallLogs",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_CdsCallLogs_ServiceId_OccurredOnUtc",
                table: "CdsCallLogs",
                columns: new[] { "ServiceId", "OccurredOnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CdsServiceInstances_ServiceId",
                table: "CdsServiceInstances",
                column: "ServiceId",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CdsTrustedIssuers_Issuer",
                table: "CdsTrustedIssuers",
                column: "Issuer",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CdsCallLogs");

            migrationBuilder.DropTable(
                name: "CdsServiceInstances");

            migrationBuilder.DropTable(
                name: "CdsTrustedIssuers");
        }
    }
}
