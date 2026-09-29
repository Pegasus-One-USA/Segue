using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class RemovePhiFromPipelineRunResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Purge before dropping, same reasoning as RemovePhiFromExecutionHistory: TRUNCATE discards the
            // heap outright, where DELETE would leave the full rows recoverable until a VACUUM that does not
            // overwrite them anyway.
            migrationBuilder.Sql(@"TRUNCATE TABLE ""PipelineRunResourceRecords"";");

            migrationBuilder.DropColumn(
                name: "FetchedJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "MappedValuesJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "NormalizedJson",
                table: "PipelineRunResourceRecords");

            // Force the table rewrite — DROP COLUMN is metadata-only on PostgreSQL. See the note in
            // RemovePhiFromExecutionHistory; backups and WAL archives are still the operator's to handle.
            migrationBuilder.Sql(@"VACUUM FULL ""PipelineRunResourceRecords"";", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FetchedJson",
                table: "PipelineRunResourceRecords",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MappedValuesJson",
                table: "PipelineRunResourceRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedJson",
                table: "PipelineRunResourceRecords",
                type: "text",
                nullable: true);
        }
    }
}
