using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartLaunchContextDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GrantedScope",
                table: "SmartLaunchLogs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PatientContextGranted",
                table: "SmartLaunchLogs",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenCacheKeyHash",
                table: "SmartLaunchLogs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrantedScope",
                table: "SmartLaunchLogs");

            migrationBuilder.DropColumn(
                name: "PatientContextGranted",
                table: "SmartLaunchLogs");

            migrationBuilder.DropColumn(
                name: "TokenCacheKeyHash",
                table: "SmartLaunchLogs");
        }
    }
}
