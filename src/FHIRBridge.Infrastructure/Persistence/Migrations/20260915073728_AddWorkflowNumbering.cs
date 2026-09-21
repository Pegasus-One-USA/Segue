using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Scaffolding this also swept in a DropTable("WorkflowNodeConfigurations") — that table is already gone
    // from the model and dropped on PostgreSQL (20260914150106_DropWorkflowNodeConfigurations), but never got
    // its SQL Server counterpart. Removed here deliberately: it is unrelated to numbering, and dropping a table
    // is not something this migration should smuggle in. It still needs its own SQL Server migration.
    public partial class AddWorkflowNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowNumber",
                table: "WorkflowDefinitions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowNumberSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNumberSequences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_WorkflowNumber",
                table: "WorkflowDefinitions",
                column: "WorkflowNumber",
                unique: true,
                filter: "[WorkflowNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNumberSequences_PeriodKey",
                table: "WorkflowNumberSequences",
                column: "PeriodKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowNumberSequences");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowDefinitions_WorkflowNumber",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "WorkflowNumber",
                table: "WorkflowDefinitions");
        }
    }
}
