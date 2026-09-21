using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLicenseRequestSingletonGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LicenseRequests_SingletonGuard",
                table: "LicenseRequests");

            migrationBuilder.DropIndex(
                name: "IX_LicenseRequests_UniqueKey",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "SingletonGuard",
                table: "LicenseRequests");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_UniqueKey",
                table: "LicenseRequests",
                column: "UniqueKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LicenseRequests_UniqueKey",
                table: "LicenseRequests");

            migrationBuilder.AddColumn<int>(
                name: "SingletonGuard",
                table: "LicenseRequests",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_SingletonGuard",
                table: "LicenseRequests",
                column: "SingletonGuard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_UniqueKey",
                table: "LicenseRequests",
                column: "UniqueKey");
        }
    }
}
