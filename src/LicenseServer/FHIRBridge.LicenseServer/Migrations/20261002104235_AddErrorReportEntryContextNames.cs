using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.LicenseServer.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorReportEntryContextNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationName",
                table: "ErrorReportEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeName",
                table: "ErrorReportEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeType",
                table: "ErrorReportEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceType",
                table: "ErrorReportEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "ErrorReportEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowName",
                table: "ErrorReportEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationName",
                table: "ErrorReportEntries");

            migrationBuilder.DropColumn(
                name: "NodeName",
                table: "ErrorReportEntries");

            migrationBuilder.DropColumn(
                name: "NodeType",
                table: "ErrorReportEntries");

            migrationBuilder.DropColumn(
                name: "ResourceType",
                table: "ErrorReportEntries");

            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "ErrorReportEntries");

            migrationBuilder.DropColumn(
                name: "WorkflowName",
                table: "ErrorReportEntries");
        }
    }
}
