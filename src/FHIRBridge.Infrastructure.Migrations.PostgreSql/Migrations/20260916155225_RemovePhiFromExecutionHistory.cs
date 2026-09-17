using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class RemovePhiFromExecutionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Purge the rows BEFORE dropping the columns. These tables are execution HISTORY — replayable
            // observability data, not a system of record — so clearing them costs past-run detail, the accepted
            // trade for getting PHI out of this database.
            //
            // TRUNCATE, not DELETE. DELETE only marks tuples dead: the full row, PHI included, stays in the heap
            // until VACUUM, and plain VACUUM reclaims that space for reuse WITHOUT overwriting it. TRUNCATE
            // discards the underlying files outright, is transactional on PostgreSQL like any other DDL here, and
            // avoids the WAL churn and table bloat a full-table DELETE causes on a busy install.
            //
            // Both tables are leaves — nothing carries a foreign key to them — so no CASCADE is needed.
            migrationBuilder.Sql(@"TRUNCATE TABLE ""FieldLineageEntries"";");
            migrationBuilder.Sql(@"TRUNCATE TABLE ""WorkflowNodeRunPayloads"";");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "WorkflowNodeRunPayloads");

            migrationBuilder.DropColumn(
                name: "DestinationValueJson",
                table: "FieldLineageEntries");

            migrationBuilder.DropColumn(
                name: "SourceValueJson",
                table: "FieldLineageEntries");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryDetailJson",
                table: "WorkflowNodeRunPayloads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceTypeCountsJson",
                table: "WorkflowNodeRunPayloads",
                type: "text",
                nullable: true);

            // ALTER TABLE ... DROP COLUMN on PostgreSQL is metadata-only: the attribute is flagged attisdropped
            // and existing tuples keep their bytes until the table is rewritten. After a TRUNCATE there should be
            // no live tuples left, but VACUUM FULL forces the rewrite so the files backing these tables cannot
            // still hold dropped-column data from before.
            //
            // NOTE FOR OPERATORS: this does NOT reach your backups, WAL archives, or replicas. Those still
            // contain the old values and must be rotated or expired separately — see docs/UPGRADE.md.
            migrationBuilder.Sql(@"VACUUM FULL ""FieldLineageEntries"";", suppressTransaction: true);
            migrationBuilder.Sql(@"VACUUM FULL ""WorkflowNodeRunPayloads"";", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryDetailJson",
                table: "WorkflowNodeRunPayloads");

            migrationBuilder.DropColumn(
                name: "ResourceTypeCountsJson",
                table: "WorkflowNodeRunPayloads");

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "WorkflowNodeRunPayloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DestinationValueJson",
                table: "FieldLineageEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceValueJson",
                table: "FieldLineageEntries",
                type: "text",
                nullable: true);
        }
    }
}
