using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageLedgerEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsageLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MonotonicTicks = table.Column<long>(type: "bigint", nullable: false),
                    UserCount = table.Column<int>(type: "int", nullable: false),
                    SourceConnectionCount = table.Column<int>(type: "int", nullable: false),
                    TenantCount = table.Column<int>(type: "int", nullable: false),
                    WorkflowCount = table.Column<int>(type: "int", nullable: false),
                    CumulativeConfiguredPipelineRunCount = table.Column<long>(type: "bigint", nullable: false),
                    CumulativeRuntimeWorkflowRunCount = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedRecordsThisMonth = table.Column<long>(type: "bigint", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageLedgerEntries_ObservedUtc",
                table: "UsageLedgerEntries",
                column: "ObservedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UsageLedgerEntries_SequenceNumber",
                table: "UsageLedgerEntries",
                column: "SequenceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageLedgerEntries");
        }
    }
}
