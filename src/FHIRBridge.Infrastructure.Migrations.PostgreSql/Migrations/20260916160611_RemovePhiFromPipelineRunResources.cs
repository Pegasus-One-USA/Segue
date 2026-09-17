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
            // Purge before dropping, same reasoning as RemovePhiFromExecutionHistory: deleting the rows first
            // leaves no row version still holding the ciphertext in unreclaimed pages.
            migrationBuilder.Sql(@"DELETE FROM ""PipelineRunResourceRecords"";");

            migrationBuilder.DropColumn(
                name: "FetchedJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "MappedValuesJson",
                table: "PipelineRunResourceRecords");

            migrationBuilder.DropColumn(
                name: "NormalizedJson",
                table: "PipelineRunResourceRecords");
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
