using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddValidationEndpointHealthLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndpointHealthChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndpointName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EndpointType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointHealthChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValidationFailureLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WarningsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    DataQualityScore = table.Column<double>(type: "float", nullable: true),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationFailureLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointHealthChecks_EndpointName",
                table: "EndpointHealthChecks",
                column: "EndpointName");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointHealthChecks_OccurredOnUtc",
                table: "EndpointHealthChecks",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointHealthChecks_Status",
                table: "EndpointHealthChecks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationFailureLogs_CorrelationId",
                table: "ValidationFailureLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationFailureLogs_OccurredOnUtc",
                table: "ValidationFailureLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationFailureLogs_ResourceType",
                table: "ValidationFailureLogs",
                column: "ResourceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndpointHealthChecks");

            migrationBuilder.DropTable(
                name: "ValidationFailureLogs");
        }
    }
}
