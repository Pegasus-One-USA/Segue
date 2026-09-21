using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowNumber",
                table: "WorkflowDefinitions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowNumberSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: true)
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
                // Re-quoted for PostgreSQL. The model's HasFilter uses SQL Server bracket syntax (the convention
                // every configuration in this solution follows, e.g. ErrorLogs' own ErrorReferenceId index) and the
                // scaffolder copies it through verbatim, which Postgres rejects — same hand-correction the
                // InitialCreate migration's filtered indexes already carry.
                filter: "\"WorkflowNumber\" IS NOT NULL");

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
