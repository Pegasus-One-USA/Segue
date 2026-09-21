using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseRequestSingletonGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SingletonGuard",
                table: "LicenseRequests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseRequests_SingletonGuard",
                table: "LicenseRequests",
                column: "SingletonGuard",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LicenseRequests_SingletonGuard",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "SingletonGuard",
                table: "LicenseRequests");
        }
    }
}
