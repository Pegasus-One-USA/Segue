using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowFieldLineageEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FieldLineageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DestinationField = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NodeOrder = table.Column<int>(type: "int", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DestinationValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<double>(type: "float", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldLineageEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_RecordedAtUtc",
                table: "FieldLineageEntries",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_WorkflowRunId",
                table: "FieldLineageEntries",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_WorkflowRunId_ResourceType_ResourceId",
                table: "FieldLineageEntries",
                columns: new[] { "WorkflowRunId", "ResourceType", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldLineageEntries");
        }
    }
}
