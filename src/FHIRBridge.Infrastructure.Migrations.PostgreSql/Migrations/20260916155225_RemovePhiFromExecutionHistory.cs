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
            // Purge the rows BEFORE dropping the columns: dropping a column removes it from the live table, but
            // old values can persist in pages the database has not yet reclaimed. Deleting first leaves no row
            // version still holding the ciphertext. These tables are execution HISTORY — replayable observability
            // data, not a system of record — so clearing them costs past-run detail, the accepted trade for
            // getting PHI out of this database.
            migrationBuilder.Sql(@"DELETE FROM ""FieldLineageEntries"";");
            migrationBuilder.Sql(@"DELETE FROM ""WorkflowNodeRunPayloads"";");

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
