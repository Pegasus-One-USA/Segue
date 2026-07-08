using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTriggeredOnUtc",
                table: "WorkflowDefinitions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerBackfillOnFirstRun",
                table: "WorkflowDefinitions",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggerIntervalMinutes",
                table: "WorkflowDefinitions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerScheduleExpression",
                table: "WorkflowDefinitions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerType",
                table: "WorkflowDefinitions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTriggeredOnUtc",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TriggerBackfillOnFirstRun",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TriggerIntervalMinutes",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TriggerScheduleExpression",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TriggerType",
                table: "WorkflowDefinitions");
        }
    }
}
