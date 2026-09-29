using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDestinationActivityLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DestinationActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DestinationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RecordCount = table.Column<int>(type: "int", nullable: true),
                    WrittenCount = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DestinationActivityLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DestinationActivityLogs_CorrelationId",
                table: "DestinationActivityLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_DestinationActivityLogs_OccurredOnUtc",
                table: "DestinationActivityLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DestinationActivityLogs_PipelineRunId",
                table: "DestinationActivityLogs",
                column: "PipelineRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DestinationActivityLogs");
        }
    }
}
