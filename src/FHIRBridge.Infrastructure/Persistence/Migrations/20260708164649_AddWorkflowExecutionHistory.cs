using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowExecutionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TriggerType",
                table: "WorkflowRuns",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggeredBy",
                table: "WorkflowRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowNodeRunPayloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowNodeRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Contract = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodeRunPayloads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeRunPayloads_RecordedAtUtc",
                table: "WorkflowNodeRunPayloads",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeRunPayloads_WorkflowRunId",
                table: "WorkflowNodeRunPayloads",
                column: "WorkflowRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowNodeRunPayloads");

            migrationBuilder.DropColumn(
                name: "TriggerType",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "TriggeredBy",
                table: "WorkflowRuns");
        }
    }
}
