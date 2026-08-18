using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HipaaCookieAuthAndWorkflowAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeIdentificationMethod",
                table: "DestinationConfigurations",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresDeIdentification",
                table: "DestinationConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkflowAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NodeType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourceIdHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    InputContract = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OutputContract = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OccurredOnUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAuditLogs_OccurredOnUtc",
                table: "WorkflowAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAuditLogs_WorkflowRunId",
                table: "WorkflowAuditLogs",
                column: "WorkflowRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowAuditLogs");

            migrationBuilder.DropColumn(
                name: "DeIdentificationMethod",
                table: "DestinationConfigurations");

            migrationBuilder.DropColumn(
                name: "RequiresDeIdentification",
                table: "DestinationConfigurations");
        }
    }
}
