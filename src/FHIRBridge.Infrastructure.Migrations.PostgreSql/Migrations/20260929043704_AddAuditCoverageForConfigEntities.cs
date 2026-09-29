using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
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
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "UserRoles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "UserRoles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "UserRoles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "UserFhirContextBindings",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "UserFhirContextBindings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "UserFhirContextBindings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "UserFhirContextBindings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ProvisionedSecrets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "ProvisionedSecrets",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "ProvisionedSecrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "LicenseRequests",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "LicenseRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "LicenseRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "LicenseRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "LicenseHistoryEntries",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "LicenseHistoryEntries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "LicenseHistoryEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "LicenseHistoryEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "ErrorResolutions",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ErrorResolutions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "timezone('utc', now())");

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "ErrorResolutions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedOnUtc",
                table: "ErrorResolutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OldValueJson",
                table: "AuditLogs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NewValueJson",
                table: "AuditLogs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
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
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "timezone('utc', now())");

            migrationBuilder.AlterColumn<string>(
                name: "OldValueJson",
                table: "AuditLogs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NewValueJson",
                table: "AuditLogs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
