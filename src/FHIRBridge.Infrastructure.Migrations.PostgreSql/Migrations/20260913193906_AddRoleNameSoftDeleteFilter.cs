using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleNameSoftDeleteFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_Name",
                table: "Roles");

            // Native Postgres predicate syntax (the scaffolded model annotation uses SQL Server's
            // "[IsDeleted] = 0" bracket/integer-boolean form, shared verbatim with the SQL Server
            // migrations project — Npgsql does not translate it, so it's corrected here to the form
            // Postgres actually accepts, matching every other soft-delete-filtered index in this project
            // (see FilterUsersExternalUserIdIndexBySoftDelete.cs, and the InitialCreate migration's
            // AllowedCorsOrigins/EhrEndpoints/SystemSettings indexes).
            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_Name",
                table: "Roles");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);
        }
    }
}
