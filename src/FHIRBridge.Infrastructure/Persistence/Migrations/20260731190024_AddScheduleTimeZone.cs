using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TriggerTimeZoneId",
                table: "WorkflowDefinitions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "ResourcePipelineRoutes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerTimeZoneId",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "ResourcePipelineRoutes");
        }
    }
}
