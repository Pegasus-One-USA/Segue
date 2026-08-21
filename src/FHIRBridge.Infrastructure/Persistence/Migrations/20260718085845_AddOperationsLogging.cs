using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationsLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiRequestLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    StackTrace = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Module = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetryHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Context = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RetryNumber = table.Column<int>(type: "int", nullable: false),
                    DelayMilliseconds = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetryHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchedulerHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchedulerId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RunTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RouteCount = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulerHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CorrelationId",
                table: "ApiRequestLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_OccurredOnUtc",
                table: "ApiRequestLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_StatusCode",
                table: "ApiRequestLogs",
                column: "StatusCode");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_CorrelationId",
                table: "ErrorLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_OccurredOnUtc",
                table: "ErrorLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_Severity",
                table: "ErrorLogs",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_RetryHistory_CorrelationId",
                table: "RetryHistory",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_RetryHistory_OccurredOnUtc",
                table: "RetryHistory",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerHistory_CorrelationId",
                table: "SchedulerHistory",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerHistory_RunTimeUtc",
                table: "SchedulerHistory",
                column: "RunTimeUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiRequestLogs");

            migrationBuilder.DropTable(
                name: "ErrorLogs");

            migrationBuilder.DropTable(
                name: "RetryHistory");

            migrationBuilder.DropTable(
                name: "SchedulerHistory");
        }
    }
}
