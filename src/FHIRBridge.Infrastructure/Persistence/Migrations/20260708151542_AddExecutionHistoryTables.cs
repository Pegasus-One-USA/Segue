using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionHistoryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineRunResourceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FetchedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NormalizedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedProfiles = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Warnings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataQualityScore = table.Column<double>(type: "float", nullable: true),
                    MasterPatientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NormalizedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MappedValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MappedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StoredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WriteStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunResourceRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRunRouteExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystemType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExtractedCount = table.Column<int>(type: "int", nullable: false),
                    MappedCount = table.Column<int>(type: "int", nullable: false),
                    WrittenCount = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunRouteExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunResourceRecords_FetchedAtUtc",
                table: "PipelineRunResourceRecords",
                column: "FetchedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunResourceRecords_RouteExecutionId",
                table: "PipelineRunResourceRecords",
                column: "RouteExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunRouteExecutions_PipelineRunId",
                table: "PipelineRunRouteExecutions",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunRouteExecutions_StartedOnUtc",
                table: "PipelineRunRouteExecutions",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunRouteExecutions_Status",
                table: "PipelineRunRouteExecutions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineRunResourceRecords");

            migrationBuilder.DropTable(
                name: "PipelineRunRouteExecutions");
        }
    }
}
