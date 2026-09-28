using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddApiRequestLogCaptureFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestBody",
                table: "ApiRequestLogs",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHeaders",
                table: "ApiRequestLogs",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseBody",
                table: "ApiRequestLogs",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseHeaders",
                table: "ApiRequestLogs",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestBody",
                table: "ApiRequestLogs");

            migrationBuilder.DropColumn(
                name: "RequestHeaders",
                table: "ApiRequestLogs");

            migrationBuilder.DropColumn(
                name: "ResponseBody",
                table: "ApiRequestLogs");

            migrationBuilder.DropColumn(
                name: "ResponseHeaders",
                table: "ApiRequestLogs");
        }
    }
}
