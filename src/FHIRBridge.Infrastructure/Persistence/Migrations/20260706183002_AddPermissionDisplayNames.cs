using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Permissions",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "PermissionGroups",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "PermissionCategories",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Normalize PermissionCategories/PermissionGroups.Name to match the owning enum member's ToString()
            // exactly (e.g. "AccessControl" instead of "Access Control") — Name is now a stable code identifier,
            // DisplayName (populated below) carries the human-readable text. Permissions.Name is untouched: it's
            // the wire-format policy code ("user.invite"), not a display concern.
            migrationBuilder.Sql("UPDATE [PermissionCategories] SET [Name] = 'AccessControl' WHERE [Name] = 'Access Control';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [Name] = 'AuditLogs' WHERE [Name] = 'Audit';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [Name] = 'SourceConnections' WHERE [Name] = 'Source Connections';");

            migrationBuilder.Sql("UPDATE [PermissionCategories] SET [DisplayName] = 'Access Control' WHERE [Name] = 'AccessControl';");
            migrationBuilder.Sql("UPDATE [PermissionCategories] SET [DisplayName] = 'Platform' WHERE [Name] = 'Platform';");

            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'User' WHERE [Name] = 'User';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Role' WHERE [Name] = 'Role';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Workflow' WHERE [Name] = 'Workflow';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Configuration' WHERE [Name] = 'Configuration';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Pipeline' WHERE [Name] = 'Pipeline';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Audit Logs' WHERE [Name] = 'AuditLogs';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Source Connections' WHERE [Name] = 'SourceConnections';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Report' WHERE [Name] = 'Report';");
            migrationBuilder.Sql("UPDATE [PermissionGroups] SET [DisplayName] = 'Payload' WHERE [Name] = 'Payload';");

            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Write Configuration' WHERE [Name] = 'configuration.write';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Execute Pipeline' WHERE [Name] = 'pipeline.execute';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Read Audit Logs' WHERE [Name] = 'auditlogs.read';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Test Source Connections' WHERE [Name] = 'sourceconnections.test';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Invite User' WHERE [Name] = 'user.invite';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'View User' WHERE [Name] = 'user.view';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Edit User' WHERE [Name] = 'user.edit';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Deactivate User' WHERE [Name] = 'user.deactivate';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Create Role' WHERE [Name] = 'role.create';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Edit Role' WHERE [Name] = 'role.edit';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Delete Role' WHERE [Name] = 'role.delete';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Assign Role' WHERE [Name] = 'role.assign';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'View Role' WHERE [Name] = 'role.view';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Create Workflow' WHERE [Name] = 'workflow.create';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Edit Workflow' WHERE [Name] = 'workflow.edit';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Delete Workflow' WHERE [Name] = 'workflow.delete';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'Run Workflow' WHERE [Name] = 'workflow.run';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'View Workflow' WHERE [Name] = 'workflow.view';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'View Report' WHERE [Name] = 'report.view';");
            migrationBuilder.Sql("UPDATE [Permissions] SET [DisplayName] = 'View Payload' WHERE [Name] = 'payload.view';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Permissions");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "PermissionGroups");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "PermissionCategories");
        }
    }
}
