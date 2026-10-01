using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEhrWriteBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Access",
                table: "SourceConnections",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Read");

            migrationBuilder.CreateTable(
                name: "EhrWriteLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    OutcomeCodes = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EhrWriteLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EhrWriteLedger_Idempotency",
                table: "EhrWriteLedgerEntries",
                columns: new[] { "TargetKey", "ResourceType", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EhrWriteLedger_State_UpdatedOnUtc",
                table: "EhrWriteLedgerEntries",
                columns: new[] { "State", "UpdatedOnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EhrWriteLedger_WorkflowRunId",
                table: "EhrWriteLedgerEntries",
                column: "WorkflowRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EhrWriteLedgerEntries");

            migrationBuilder.DropColumn(
                name: "Access",
                table: "SourceConnections");
        }
    }
}
