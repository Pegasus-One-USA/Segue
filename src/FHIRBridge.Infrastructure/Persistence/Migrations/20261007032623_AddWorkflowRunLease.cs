using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRunLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAt",
                table: "WorkflowRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "WorkflowRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "WorkflowRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "WorkflowRuns");
        }
    }
}
