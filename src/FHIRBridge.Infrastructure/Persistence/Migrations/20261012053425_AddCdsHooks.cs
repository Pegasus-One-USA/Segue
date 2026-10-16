using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ServiceInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HandlerKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Hook = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HookInstance = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Issuer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TrustedIssuerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: false),
                    CardCount = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CdsCallLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CdsServiceInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HandlerKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Hook = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CardSourceLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CardSourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AllowedIssuerIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CdsServiceInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CdsTrustedIssuers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Vendor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    JwksUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    JwksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Audience = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_CdsTrustedIssuers_Issuer",
                table: "CdsTrustedIssuers",
                column: "Issuer",
                unique: true,
                filter: "[IsDeleted] = 0");
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
