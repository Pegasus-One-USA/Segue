using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedByCreatedOnUtcDefaults : Migration
    {
        private static readonly string[] AuditableTables =
        [
            "WorkflowDefinitions", "WebhookConfigurations", "Users", "SystemSettings", "SourceConnections",
            "SourceConfigurations", "SourceCapabilityProfiles", "Roles", "ResourcePipelineRoutes", "Permissions",
            "PermissionGroups", "PermissionCategories", "PermissionAllocations", "NotificationSettings",
            "MappingProfiles", "EhrEndpoints", "DestinationConfigurations", "AllowedCorsOrigins", "AlertRules",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill any existing NULL CreatedBy first — these predate CreatedBy being reliably stamped (see
            // AuditingSaveChangesInterceptor/CurrentUserInfo.AuditName) and have no real actor to recover, so
            // "system" (this migration's own DB-level default for future inserts) is the only honest value.
            // Without this, ALTER COLUMN ... NOT NULL below fails outright if any row still has a NULL.
            foreach (var table in AuditableTables)
            {
                migrationBuilder.Sql($"UPDATE [{table}] SET [CreatedBy] = 'system' WHERE [CreatedBy] IS NULL;");
            }

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "WorkflowDefinitions",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "WorkflowDefinitions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "WebhookConfigurations",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "WebhookConfigurations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "Users",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "Users",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SystemSettings",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SystemSettings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SourceConnections",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SourceConnections",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SourceConfigurations",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SourceConfigurations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SourceCapabilityProfiles",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SourceCapabilityProfiles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "Roles",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "Roles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ResourcePipelineRoutes",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "ResourcePipelineRoutes",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "Permissions",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "Permissions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "PermissionGroups",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "PermissionGroups",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "PermissionCategories",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "PermissionCategories",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "PermissionAllocations",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "PermissionAllocations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "NotificationSettings",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "NotificationSettings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "MappingProfiles",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "MappingProfiles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "EhrEndpoints",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "EhrEndpoints",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "DestinationConfigurations",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "DestinationConfigurations",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "AllowedCorsOrigins",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "AllowedCorsOrigins",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "AlertRules",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "AlertRules",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "system",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "WorkflowDefinitions",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "WorkflowDefinitions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "WebhookConfigurations",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "WebhookConfigurations",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "Users",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SystemSettings",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SystemSettings",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SourceConnections",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SourceConnections",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SourceConfigurations",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SourceConfigurations",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "SourceCapabilityProfiles",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "SourceCapabilityProfiles",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "Roles",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "Roles",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "ResourcePipelineRoutes",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "ResourcePipelineRoutes",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "Permissions",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "Permissions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "PermissionGroups",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "PermissionGroups",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "PermissionCategories",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "PermissionCategories",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "PermissionAllocations",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "PermissionAllocations",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "NotificationSettings",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "NotificationSettings",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "MappingProfiles",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "MappingProfiles",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "EhrEndpoints",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "EhrEndpoints",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "DestinationConfigurations",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "DestinationConfigurations",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "AllowedCorsOrigins",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "AllowedCorsOrigins",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedOnUtc",
                table: "AlertRules",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedBy",
                table: "AlertRules",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldDefaultValue: "system");
        }
    }
}
