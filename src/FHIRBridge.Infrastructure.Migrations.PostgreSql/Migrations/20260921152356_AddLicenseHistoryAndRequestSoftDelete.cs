using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseHistoryAndRequestSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedOnUtc",
                table: "LicenseRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "LicenseRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedOnUtc",
                table: "LicenseHistoryEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "LicenseHistoryEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_IsDeleted",
                table: "LicenseRequests",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseHistoryEntries_IsDeleted",
                table: "LicenseHistoryEntries",
                column: "IsDeleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LicenseRequests_IsDeleted",
                table: "LicenseRequests");

            migrationBuilder.DropIndex(
                name: "IX_LicenseHistoryEntries_IsDeleted",
                table: "LicenseHistoryEntries");

            migrationBuilder.DropColumn(
                name: "DeletedOnUtc",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "DeletedOnUtc",
                table: "LicenseHistoryEntries");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "LicenseHistoryEntries");
        }
    }
}
