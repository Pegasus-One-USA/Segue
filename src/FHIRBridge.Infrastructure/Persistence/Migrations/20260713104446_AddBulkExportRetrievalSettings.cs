using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkExportRetrievalSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RetrievalExportScope",
                table: "SourceConnections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalGroupId",
                table: "SourceConnections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalOutputFormat",
                table: "SourceConnections",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalPatientIds",
                table: "SourceConnections",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetrievalExportScope",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalGroupId",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalOutputFormat",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RetrievalPatientIds",
                table: "SourceConnections");
        }
    }
}
