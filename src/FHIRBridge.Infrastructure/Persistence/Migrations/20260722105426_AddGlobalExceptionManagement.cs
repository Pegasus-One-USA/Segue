using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalExceptionManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ErrorLogs",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndpointId",
                table: "ErrorLogs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorReferenceId",
                table: "ErrorLogs",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionId",
                table: "ErrorLogs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "ErrorLogs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpanId",
                table: "ErrorLogs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "ErrorLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserFriendlyMessage",
                table: "ErrorLogs",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowId",
                table: "ErrorLogs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ErrorResolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ErrorReferenceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResolvedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorResolutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_Category",
                table: "ErrorLogs",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_ErrorReferenceId",
                table: "ErrorLogs",
                column: "ErrorReferenceId",
                unique: true,
                filter: "[ErrorReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_ExecutionId",
                table: "ErrorLogs",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorResolutions_ErrorReferenceId",
                table: "ErrorResolutions",
                column: "ErrorReferenceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErrorResolutions");

            migrationBuilder.DropIndex(
                name: "IX_ErrorLogs_Category",
                table: "ErrorLogs");

            migrationBuilder.DropIndex(
                name: "IX_ErrorLogs_ErrorReferenceId",
                table: "ErrorLogs");

            migrationBuilder.DropIndex(
                name: "IX_ErrorLogs_ExecutionId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "EndpointId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "ErrorReferenceId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "SpanId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "UserFriendlyMessage",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "ErrorLogs");
        }
    }
}
