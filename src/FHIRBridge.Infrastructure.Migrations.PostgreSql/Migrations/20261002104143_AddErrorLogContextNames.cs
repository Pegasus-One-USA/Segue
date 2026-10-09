using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorLogContextNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationName",
                table: "ErrorLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeName",
                table: "ErrorLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeType",
                table: "ErrorLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceType",
                table: "ErrorLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "ErrorLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowName",
                table: "ErrorLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationName",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "NodeName",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "NodeType",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "ResourceType",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "WorkflowName",
                table: "ErrorLogs");
        }
    }
}
