using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceApplicationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicationType",
                table: "SourceConnections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LaunchUrl",
                table: "SourceConnections",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedirectUris",
                table: "SourceConnections",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustedIssuers",
                table: "SourceConnections",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplicationType",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "LaunchUrl",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RedirectUris",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "TrustedIssuers",
                table: "SourceConnections");
        }
    }
}
