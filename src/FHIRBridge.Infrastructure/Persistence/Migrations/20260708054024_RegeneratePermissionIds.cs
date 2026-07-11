using FHIRBridge.Application.Security;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Replaces every built-in Permission's hand-picked <c>SeededSecurityIds.*PermissionId</c> guid with a
    /// deterministic one derived from its Group+Action pair (see <see cref="PermissionTaxonomy.BuildPermissionId"/>),
    /// so a future permission never needs a manually-minted id. Remaps both <c>Permissions.Id</c> and every
    /// <c>PermissionAllocations.PermissionId</c> that references it, so existing role/user grants survive intact.
    /// </summary>
    public partial class RegeneratePermissionIds : Migration
    {
        // (old hand-picked id, its Group+Action) for all 20 built-in permissions, exactly as they were declared
        // in SeededSecurityIds/RbacSeedData before this migration. The new id is computed live via
        // PermissionTaxonomy.BuildPermissionId, so this migration and the current C# seed data can never disagree.
        private static readonly (Guid OldId, PermissionGroupCode Group, PermissionActionCode Action)[] Remap =
        [
            (new Guid("20000000-0000-0000-0000-000000000003"), PermissionGroupCode.Configuration, PermissionActionCode.Write),
            (new Guid("20000000-0000-0000-0000-000000000004"), PermissionGroupCode.Pipeline, PermissionActionCode.Execute),
            (new Guid("20000000-0000-0000-0000-000000000005"), PermissionGroupCode.AuditLogs, PermissionActionCode.Read),
            (new Guid("20000000-0000-0000-0000-000000000006"), PermissionGroupCode.SourceConnections, PermissionActionCode.Test),
            (new Guid("20000000-0000-0000-0001-000000000001"), PermissionGroupCode.User, PermissionActionCode.Invite),
            (new Guid("20000000-0000-0000-0001-000000000002"), PermissionGroupCode.User, PermissionActionCode.View),
            (new Guid("20000000-0000-0000-0001-000000000003"), PermissionGroupCode.User, PermissionActionCode.Edit),
            (new Guid("20000000-0000-0000-0001-000000000004"), PermissionGroupCode.User, PermissionActionCode.Deactivate),
            (new Guid("20000000-0000-0000-0002-000000000001"), PermissionGroupCode.Role, PermissionActionCode.Create),
            (new Guid("20000000-0000-0000-0002-000000000002"), PermissionGroupCode.Role, PermissionActionCode.Edit),
            (new Guid("20000000-0000-0000-0002-000000000003"), PermissionGroupCode.Role, PermissionActionCode.Delete),
            (new Guid("20000000-0000-0000-0002-000000000004"), PermissionGroupCode.Role, PermissionActionCode.Assign),
            (new Guid("20000000-0000-0000-0002-000000000005"), PermissionGroupCode.Role, PermissionActionCode.View),
            (new Guid("20000000-0000-0000-0003-000000000001"), PermissionGroupCode.Workflow, PermissionActionCode.Create),
            (new Guid("20000000-0000-0000-0003-000000000002"), PermissionGroupCode.Workflow, PermissionActionCode.Edit),
            (new Guid("20000000-0000-0000-0003-000000000003"), PermissionGroupCode.Workflow, PermissionActionCode.Delete),
            (new Guid("20000000-0000-0000-0003-000000000004"), PermissionGroupCode.Workflow, PermissionActionCode.Run),
            (new Guid("20000000-0000-0000-0003-000000000005"), PermissionGroupCode.Workflow, PermissionActionCode.View),
            (new Guid("20000000-0000-0000-0005-000000000001"), PermissionGroupCode.Report, PermissionActionCode.View),
            (new Guid("20000000-0000-0000-0006-000000000001"), PermissionGroupCode.Payload, PermissionActionCode.View),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PermissionAllocations_Permissions_PermissionId",
                table: "PermissionAllocations");

            foreach (var (oldId, group, action) in Remap)
            {
                var newId = PermissionTaxonomy.BuildPermissionId(group, action);

                migrationBuilder.Sql($"UPDATE [PermissionAllocations] SET [PermissionId] = '{newId}' WHERE [PermissionId] = '{oldId}';");
                migrationBuilder.Sql($"UPDATE [Permissions] SET [Id] = '{newId}' WHERE [Id] = '{oldId}';");
            }

            migrationBuilder.AddForeignKey(
                name: "FK_PermissionAllocations_Permissions_PermissionId",
                table: "PermissionAllocations",
                column: "PermissionId",
                principalTable: "Permissions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PermissionAllocations_Permissions_PermissionId",
                table: "PermissionAllocations");

            foreach (var (oldId, group, action) in Remap)
            {
                var newId = PermissionTaxonomy.BuildPermissionId(group, action);

                migrationBuilder.Sql($"UPDATE [PermissionAllocations] SET [PermissionId] = '{oldId}' WHERE [PermissionId] = '{newId}';");
                migrationBuilder.Sql($"UPDATE [Permissions] SET [Id] = '{oldId}' WHERE [Id] = '{newId}';");
            }

            migrationBuilder.AddForeignKey(
                name: "FK_PermissionAllocations_Permissions_PermissionId",
                table: "PermissionAllocations",
                column: "PermissionId",
                principalTable: "Permissions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
