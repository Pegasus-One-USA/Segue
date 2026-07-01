using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationalAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourcePipelineRouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ResourceCount = table.Column<int>(type: "int", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RetentionDays = table.Column<int>(type: "int", nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Region = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserActivityAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Activity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivityAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsLocalLoginEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    PasswordResetTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PasswordResetTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastLoginOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPasswordChangedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PasswordExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MfaEnabled = table.Column<bool>(type: "bit", nullable: false),
                    InvitationTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InvitationTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefreshTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefreshTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantUsers_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantUsers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TenantUsers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Permissions",
                columns: new[] { "Id", "Category", "CreatedBy", "CreatedOnUtc", "DeletedBy", "DeletedOnUtc", "Description", "IsDeleted", "IsSystem", "ModifiedBy", "ModifiedOnUtc", "Name" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), "Tenancy", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Read tenant configuration.", false, true, null, null, "tenants.read" },
                    { new Guid("20000000-0000-0000-0000-000000000002"), "Tenancy", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Create and update tenants.", false, true, null, null, "tenants.write" },
                    { new Guid("20000000-0000-0000-0000-000000000003"), "Configuration", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Manage source, destination, mapping, webhook, and route configuration.", false, true, null, null, "configuration.write" },
                    { new Guid("20000000-0000-0000-0000-000000000004"), "Pipeline", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Execute configured pipeline routes.", false, true, null, null, "pipeline.execute" },
                    { new Guid("20000000-0000-0000-0000-000000000005"), "Audit", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Read operational audit logs.", false, true, null, null, "auditlogs.read" },
                    { new Guid("20000000-0000-0000-0000-000000000006"), "Configuration", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Test source system connectivity.", false, true, null, null, "sourceconnections.test" },
                    { new Guid("20000000-0000-0000-0001-000000000001"), "User", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Invite a new user to the tenant.", false, true, null, null, "user.invite" },
                    { new Guid("20000000-0000-0000-0001-000000000002"), "User", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "View the list of users.", false, true, null, null, "user.view" },
                    { new Guid("20000000-0000-0000-0001-000000000003"), "User", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Update a user's profile information.", false, true, null, null, "user.edit" },
                    { new Guid("20000000-0000-0000-0001-000000000004"), "User", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Deactivate a user account.", false, true, null, null, "user.deactivate" },
                    { new Guid("20000000-0000-0000-0002-000000000001"), "Role", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Create a new custom role.", false, true, null, null, "role.create" },
                    { new Guid("20000000-0000-0000-0002-000000000002"), "Role", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Edit an existing role.", false, true, null, null, "role.edit" },
                    { new Guid("20000000-0000-0000-0002-000000000003"), "Role", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Delete a custom role.", false, true, null, null, "role.delete" },
                    { new Guid("20000000-0000-0000-0002-000000000004"), "Role", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Assign or remove roles from users.", false, true, null, null, "role.assign" },
                    { new Guid("20000000-0000-0000-0002-000000000005"), "Role", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "View roles and their permissions.", false, true, null, null, "role.view" },
                    { new Guid("20000000-0000-0000-0003-000000000001"), "Workflow", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Create a new workflow.", false, true, null, null, "workflow.create" },
                    { new Guid("20000000-0000-0000-0003-000000000002"), "Workflow", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Edit an existing workflow.", false, true, null, null, "workflow.edit" },
                    { new Guid("20000000-0000-0000-0003-000000000003"), "Workflow", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Delete a workflow.", false, true, null, null, "workflow.delete" },
                    { new Guid("20000000-0000-0000-0003-000000000004"), "Workflow", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Execute a workflow.", false, true, null, null, "workflow.run" },
                    { new Guid("20000000-0000-0000-0003-000000000005"), "Workflow", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "View workflow details.", false, true, null, null, "workflow.view" },
                    { new Guid("20000000-0000-0000-0004-000000000001"), "Tenant", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Update organization settings.", false, true, null, null, "tenant.settings.edit" },
                    { new Guid("20000000-0000-0000-0004-000000000002"), "Tenant", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "View billing and subscription information.", false, true, null, null, "tenant.billing.view" },
                    { new Guid("20000000-0000-0000-0005-000000000001"), "Report", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "View reports and analytics.", false, true, null, null, "report.view" },
                    { new Guid("20000000-0000-0000-0006-000000000001"), "Payload", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "View data payloads from workflow runs.", false, true, null, null, "payload.view" }
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "CreatedBy", "CreatedOnUtc", "DeletedBy", "DeletedOnUtc", "Description", "IsDefault", "IsDeleted", "IsEnabled", "IsSystem", "ModifiedBy", "ModifiedOnUtc", "Name", "TenantId" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Full platform administrator across all tenants.", false, false, true, true, null, null, "GlobalAdmin", null },
                    { new Guid("10000000-0000-0000-0000-000000000002"), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Administers configuration and users within a tenant.", false, false, true, true, null, null, "TenantAdmin", null },
                    { new Guid("10000000-0000-0000-0000-000000000003"), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Builds and runs pipeline configurations within a tenant.", false, false, true, true, null, null, "PipelineEngineer", null },
                    { new Guid("10000000-0000-0000-0000-000000000004"), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Runs pipelines and reviews data and audit output.", false, false, true, true, null, null, "Analyst", null },
                    { new Guid("10000000-0000-0000-0000-000000000005"), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Read-only access to configuration and audit logs.", false, false, true, true, null, null, "Auditor", null }
                });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionId", "RoleId", "IsEnabled" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0000-000000000002"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0000-000000000003"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0000-000000000006"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0001-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0001-000000000002"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0001-000000000003"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0001-000000000004"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0002-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0002-000000000002"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0002-000000000003"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0002-000000000004"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0002-000000000005"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0003-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0003-000000000002"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0003-000000000003"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0003-000000000004"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0003-000000000005"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0004-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0004-000000000002"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0005-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0006-000000000001"), new Guid("10000000-0000-0000-0000-000000000001"), true },
                    { new Guid("20000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0000-000000000002"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0000-000000000003"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0000-000000000006"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0001-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0001-000000000002"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0001-000000000003"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0001-000000000004"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0002-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0002-000000000002"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0002-000000000003"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0002-000000000004"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0002-000000000005"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0003-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0003-000000000002"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0003-000000000003"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0003-000000000004"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0003-000000000005"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0004-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0004-000000000002"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0005-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0006-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), true },
                    { new Guid("20000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0000-000000000003"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0000-000000000006"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0003-000000000001"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0003-000000000002"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0003-000000000003"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0003-000000000004"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0003-000000000005"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0006-000000000001"), new Guid("10000000-0000-0000-0000-000000000003"), true },
                    { new Guid("20000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0003-000000000004"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0003-000000000005"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0005-000000000001"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0006-000000000001"), new Guid("10000000-0000-0000-0000-000000000004"), true },
                    { new Guid("20000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000005"), true },
                    { new Guid("20000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000005"), true },
                    { new Guid("20000000-0000-0000-0003-000000000005"), new Guid("10000000-0000-0000-0000-000000000005"), true },
                    { new Guid("20000000-0000-0000-0005-000000000001"), new Guid("10000000-0000-0000-0000-000000000005"), true }
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAuditLogs_OccurredOnUtc",
                table: "OperationalAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAuditLogs_TenantId",
                table: "OperationalAuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Name",
                table: "Permissions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionId",
                table: "RolePermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_RoleId",
                table: "TenantUsers",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_TenantId_UserId",
                table: "TenantUsers",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_UserId",
                table: "TenantUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_OccurredOnUtc",
                table: "UserActivityAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_TenantId",
                table: "UserActivityAuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_UserId",
                table: "UserActivityAuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalUserId",
                table: "Users",
                column: "ExternalUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_RefreshTokenHash",
                table: "Users",
                column: "RefreshTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationalAuditLogs");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "TenantUsers");

            migrationBuilder.DropTable(
                name: "UserActivityAuditLogs");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
