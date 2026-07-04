using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionCategoryGroupHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Permissions_PermissionCategories_CategoryId",
                table: "Permissions");

            // The 8 rows previously seeded into PermissionCategories (Configuration, Pipeline, Audit, User,
            // Role, Workflow, Report, Payload) are superseded by the new PermissionGroups table below, which
            // reuses the same ids (see SeededSecurityIds.*GroupId). Delete them here so PermissionCategories
            // ends up holding only the new top-level categories (AccessControl, Platform) once
            // IRbacBootstrapper re-seeds on next boot. No-op on a database that never ran the old seed.
            migrationBuilder.DeleteData(
                table: "PermissionCategories",
                keyColumn: "Id",
                keyValues: new object[]
                {
                    new Guid("30000000-0000-0000-0000-000000000001"),
                    new Guid("30000000-0000-0000-0000-000000000002"),
                    new Guid("30000000-0000-0000-0000-000000000003"),
                    new Guid("30000000-0000-0000-0000-000000000004"),
                    new Guid("30000000-0000-0000-0000-000000000005"),
                    new Guid("30000000-0000-0000-0000-000000000006"),
                    new Guid("30000000-0000-0000-0000-000000000007"),
                    new Guid("30000000-0000-0000-0000-000000000008"),
                });

            migrationBuilder.RenameColumn(
                name: "CategoryId",
                table: "Permissions",
                newName: "GroupId");

            migrationBuilder.RenameIndex(
                name: "IX_Permissions_CategoryId",
                table: "Permissions",
                newName: "IX_Permissions_GroupId");

            migrationBuilder.AddColumn<bool>(
                name: "IsVisible",
                table: "Permissions",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVisible",
                table: "PermissionCategories",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "PermissionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_PermissionGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionGroups_PermissionCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "PermissionCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGroups_CategoryId",
                table: "PermissionGroups",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGroups_Name",
                table: "PermissionGroups",
                column: "Name",
                unique: true);

            // A database that has already booted once has Permissions rows whose (renamed) GroupId still
            // holds the old category ids, but PermissionGroups is still empty at this point — adding the FK
            // below would fail immediately with a constraint violation. Seed the top-level categories and
            // groups here, using the exact ids RbacSeedData/SeededSecurityIds already assign them, so the FK
            // has something to point at; IRbacBootstrapper's id-based idempotency check skips these on next
            // boot instead of duplicating them.
            migrationBuilder.InsertData(
                table: "PermissionCategories",
                columns: new[] { "Id", "Name", "Description", "IsVisible", "CreatedOnUtc", "CreatedBy", "ModifiedOnUtc", "ModifiedBy", "IsDeleted", "DeletedOnUtc", "DeletedBy" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-000000000001"), "Access Control", null, true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("40000000-0000-0000-0000-000000000002"), "Platform", null, true, SeedTimestamp, null, null, null, false, null, null },
                });

            migrationBuilder.InsertData(
                table: "PermissionGroups",
                columns: new[] { "Id", "Name", "Description", "CategoryId", "IsVisible", "CreatedOnUtc", "CreatedBy", "ModifiedOnUtc", "ModifiedBy", "IsDeleted", "DeletedOnUtc", "DeletedBy" },
                values: new object[,]
                {
                    { new Guid("30000000-0000-0000-0000-000000000001"), "Configuration", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "Pipeline", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "Audit", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "User", null, new Guid("40000000-0000-0000-0000-000000000001"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000005"), "Role", null, new Guid("40000000-0000-0000-0000-000000000001"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000006"), "Workflow", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000007"), "Report", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000008"), "Payload", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                    { new Guid("30000000-0000-0000-0000-000000000009"), "Source Connections", null, new Guid("40000000-0000-0000-0000-000000000002"), true, SeedTimestamp, null, null, null, false, null, null },
                });

            migrationBuilder.AddForeignKey(
                name: "FK_Permissions_PermissionGroups_GroupId",
                table: "Permissions",
                column: "GroupId",
                principalTable: "PermissionGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        private static readonly DateTime SeedTimestamp = new(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Permissions_PermissionGroups_GroupId",
                table: "Permissions");

            migrationBuilder.DropTable(
                name: "PermissionGroups");

            migrationBuilder.DeleteData(
                table: "PermissionCategories",
                keyColumn: "Id",
                keyValues: new object[]
                {
                    new Guid("40000000-0000-0000-0000-000000000001"),
                    new Guid("40000000-0000-0000-0000-000000000002"),
                });

            migrationBuilder.DropColumn(
                name: "IsVisible",
                table: "Permissions");

            migrationBuilder.DropColumn(
                name: "IsVisible",
                table: "PermissionCategories");

            migrationBuilder.RenameColumn(
                name: "GroupId",
                table: "Permissions",
                newName: "CategoryId");

            migrationBuilder.RenameIndex(
                name: "IX_Permissions_GroupId",
                table: "Permissions",
                newName: "IX_Permissions_CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Permissions_PermissionCategories_CategoryId",
                table: "Permissions",
                column: "CategoryId",
                principalTable: "PermissionCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
