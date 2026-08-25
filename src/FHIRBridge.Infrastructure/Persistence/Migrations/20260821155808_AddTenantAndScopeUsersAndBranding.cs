using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAndScopeUsersAndBranding : Migration
    {
        // The well-known Default Tenant id — must match SeededSecurityIds.DefaultTenantId exactly. Every
        // pre-existing Users/BrandConfigurations row is assigned to this tenant so migrating an existing
        // database changes zero observable behavior: one deployment, one tenant, exactly as it already
        // behaved, just now backed by a real Tenant row instead of an absent concept.
        private static readonly Guid DefaultTenantId = new("20000000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Create Tenants first and seed the Default Tenant row BEFORE any FK to it can be added —
            // the two AddColumn calls below use this same id as their column DEFAULT, which is what lets
            // SQL Server backfill every existing Users/BrandConfigurations row in the same statement that
            // adds the column, with no separate nullable-then-UPDATE-then-tighten step required.
            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
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

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Code",
                table: "Tenants",
                column: "Code",
                unique: true);

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "Name", "Code", "IsActive", "CreatedBy", "IsDeleted" },
                values: new object[] { DefaultTenantId, "Default Tenant", "default", true, "system", false });

            // 2) Add TenantId to Users/BrandConfigurations with the Default Tenant's id as the column
            // DEFAULT — SQL Server populates every existing row with this value as part of the ADD COLUMN
            // statement itself, since it's a real, already-inserted, valid Tenant id (not Guid.Empty, which
            // would violate the FK added in step 3 the moment it's checked against existing data).
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Users",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: DefaultTenantId);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "BrandConfigurations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: DefaultTenantId);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BrandConfigurations_TenantId",
                table: "BrandConfigurations",
                column: "TenantId",
                unique: true);

            // 3) FKs last, now that every row (old and the seeded Default Tenant itself) already satisfies
            // them. Users -> Tenant is RESTRICT (a tenant with users can never be deleted out from under
            // them — see TenantsService.DeleteAsync's explicit check, this is the DB-level backstop behind
            // it); BrandConfigurations -> Tenant is CASCADE (a tenant's branding row has no purpose once
            // the tenant itself is gone, and the RESTRICT above already blocks the destructive case).
            migrationBuilder.AddForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BrandConfigurations_Tenants_TenantId",
                table: "BrandConfigurations",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BrandConfigurations_Tenants_TenantId",
                table: "BrandConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_BrandConfigurations_TenantId",
                table: "BrandConfigurations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "BrandConfigurations");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
