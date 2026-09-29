using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditCoverageForConfigEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "UserRoles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "UserRoles",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "UserRoles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "UserRoles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "UserFhirContextBindings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "UserFhirContextBindings",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "UserFhirContextBindings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "UserFhirContextBindings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ProvisionedSecrets",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "ProvisionedSecrets",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "ProvisionedSecrets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "LicenseRequests",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "LicenseRequests",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "LicenseRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "LicenseRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "LicenseHistoryEntries",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "LicenseHistoryEntries",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "LicenseHistoryEntries",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "LicenseHistoryEntries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "ErrorResolutions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ErrorResolutions",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "ErrorResolutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "ErrorResolutions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OldValueJson",
                table: "AuditLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NewValueJson",
                table: "AuditLogs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "UserRoles");

            migrationBuilder.DropColumn(
                name: "CreatedOnUtc",
                table: "UserRoles");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "UserRoles");

            migrationBuilder.DropColumn(
                name: "ModifiedOnUtc",
                table: "UserRoles");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "UserFhirContextBindings");

            migrationBuilder.DropColumn(
                name: "CreatedOnUtc",
                table: "UserFhirContextBindings");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "UserFhirContextBindings");

            migrationBuilder.DropColumn(
                name: "ModifiedOnUtc",
                table: "UserFhirContextBindings");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ProvisionedSecrets");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "ProvisionedSecrets");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "CreatedOnUtc",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "ModifiedOnUtc",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "LicenseHistoryEntries");

            migrationBuilder.DropColumn(
                name: "CreatedOnUtc",
                table: "LicenseHistoryEntries");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "LicenseHistoryEntries");

            migrationBuilder.DropColumn(
                name: "ModifiedOnUtc",
                table: "LicenseHistoryEntries");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ErrorResolutions");

            migrationBuilder.DropColumn(
                name: "CreatedOnUtc",
                table: "ErrorResolutions");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "ErrorResolutions");

            migrationBuilder.DropColumn(
                name: "ModifiedOnUtc",
                table: "ErrorResolutions");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ProvisionedSecrets",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "OldValueJson",
                table: "AuditLogs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NewValueJson",
                table: "AuditLogs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
