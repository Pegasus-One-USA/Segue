using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseRequestAndIssuedLicenseRequestHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestHost",
                table: "LicenseRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHost",
                table: "IssuedLicenses",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestHost",
                table: "LicenseRequests");

            migrationBuilder.DropColumn(
                name: "RequestHost",
                table: "IssuedLicenses");
        }
    }
}
