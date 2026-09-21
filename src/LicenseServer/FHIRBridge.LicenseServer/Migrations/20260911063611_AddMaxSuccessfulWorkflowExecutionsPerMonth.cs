using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxSuccessfulWorkflowExecutionsPerMonth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxSuccessfulWorkflowExecutionsPerMonth",
                table: "IssuedLicenses",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxSuccessfulWorkflowExecutionsPerMonth",
                table: "IssuedLicenses");
        }
    }
}
