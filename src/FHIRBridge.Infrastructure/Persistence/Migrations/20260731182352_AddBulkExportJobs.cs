using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkExportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BulkExportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExportRequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PollAttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextPollNotBeforeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    KickedOffOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriorNodeOutputsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContextJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkExportJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkExportJobs_CorrelationId",
                table: "BulkExportJobs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkExportJobs_Status_NextPollNotBeforeUtc",
                table: "BulkExportJobs",
                columns: new[] { "Status", "NextPollNotBeforeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkExportJobs_WorkflowRunId",
                table: "BulkExportJobs",
                column: "WorkflowRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BulkExportJobs");
        }
    }
}
