using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseRequestSubmissionCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SubmissionCount",
                table: "LicenseRequests",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubmissionCount",
                table: "LicenseRequests");
        }
    }
}
