using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ObservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MonotonicTicks = table.Column<long>(type: "bigint", nullable: false),
                    UserCount = table.Column<int>(type: "integer", nullable: false),
                    SourceConnectionCount = table.Column<int>(type: "integer", nullable: false),
                    TenantCount = table.Column<int>(type: "integer", nullable: false),
                    WorkflowCount = table.Column<int>(type: "integer", nullable: false),
                    CumulativeConfiguredPipelineRunCount = table.Column<long>(type: "bigint", nullable: false),
                    CumulativeRuntimeWorkflowRunCount = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedRecordsThisMonth = table.Column<long>(type: "bigint", nullable: false),
                    PreviousHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
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
