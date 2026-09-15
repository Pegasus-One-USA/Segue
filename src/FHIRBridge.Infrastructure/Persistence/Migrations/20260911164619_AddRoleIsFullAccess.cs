using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleIsFullAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFullAccess",
                table: "Roles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // RBAC redesign backfill: the built-in SuperAdmin/Admin rows (well-known ids from
            // SeededSecurityIds — same ids RbacSeedData/SystemRoleDefaultPermissions already key off of)
            // start with the flag set. Every other role (including custom ones) keeps the column default
            // of false. This flag is actively read by CachedUserPermissionsProvider and the
            // UnifiedAdminAuthorizationHandler/SuperAdminOnlyAuthorizationHandler authorization handlers,
            // and its grant/revoke is gated through RoleManagementService — see Role.IsFullAccess's doc
            // comment.
            migrationBuilder.Sql(
                "UPDATE [Roles] SET [IsFullAccess] = 1 " +
                "WHERE [Id] IN ('10000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000002');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFullAccess",
                table: "Roles");
        }
    }
}
