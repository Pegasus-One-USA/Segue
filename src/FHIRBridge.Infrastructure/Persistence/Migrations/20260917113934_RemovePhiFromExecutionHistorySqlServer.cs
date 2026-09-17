using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemovePhiFromExecutionHistorySqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server counterpart of the PostgreSql project's RemovePhiFromExecutionHistory /
            // RemovePhiFromPipelineRunResources pair. Both providers need their own migration: SQL Server
            // resolves migrations from this assembly (no MigrationsAssembly is configured for it in
            // DependencyInjection), so without this file a SQL Server deployment kept every PHI column AND
            // began failing inserts — PayloadJson and FetchedJson are NOT NULL with no default, and the
            // entities no longer supply them.
            //
            // Purge before dropping, same reasoning as the PostgreSQL pair. TRUNCATE rather than DELETE:
            // DELETE leaves ghost records holding the full row until they are cleaned up, and generates a
            // large log volume on a busy install. These tables are execution HISTORY — replayable
            // observability data, not a system of record.
            //
            // All three are leaf tables (nothing holds a foreign key to them), so TRUNCATE is permitted.
            migrationBuilder.Sql("TRUNCATE TABLE [FieldLineageEntries];");
            migrationBuilder.Sql("TRUNCATE TABLE [WorkflowNodeRunPayloads];");
            migrationBuilder.Sql("TRUNCATE TABLE [PipelineRunResourceRecords];");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "WorkflowNodeRunPayloads");

            migrationBuilder.DropColumn(
                name: "FetchedJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "MappedValuesJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "NormalizedJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "DestinationValueJson",
                table: "FieldLineageEntries");

            migrationBuilder.DropColumn(
                name: "SourceValueJson",
                table: "FieldLineageEntries");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryDetailJson",
                table: "WorkflowNodeRunPayloads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceTypeCountsJson",
                table: "WorkflowNodeRunPayloads",
                type: "nvarchar(max)",
                nullable: true);

            // DROP COLUMN on SQL Server is metadata-only, and these were nvarchar(max): their values lived in
            // off-row LOB pages that the drop does not release. Rebuilding each table (the SQL Server analogue
            // of the PostgreSQL side's VACUUM FULL) forces those pages to be reclaimed.
            //
            // NOTE FOR OPERATORS: this reaches only the live database. Backups, log backups, and any replicas
            // still hold the old values and must be rotated or expired separately — see docs/UPGRADE.md.
            migrationBuilder.Sql("ALTER TABLE [FieldLineageEntries] REBUILD;");
            migrationBuilder.Sql("ALTER TABLE [WorkflowNodeRunPayloads] REBUILD;");
            migrationBuilder.Sql("ALTER TABLE [PipelineRunResourceRecords] REBUILD;");
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
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FetchedJson",
                table: "PipelineRunResourceRecords",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MappedValuesJson",
                table: "PipelineRunResourceRecords",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedJson",
                table: "PipelineRunResourceRecords",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationValueJson",
                table: "FieldLineageEntries",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceValueJson",
                table: "FieldLineageEntries",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
