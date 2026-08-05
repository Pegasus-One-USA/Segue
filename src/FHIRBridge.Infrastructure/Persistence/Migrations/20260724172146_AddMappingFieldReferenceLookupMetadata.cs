using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMappingFieldReferenceLookupMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferenceLookupKeyColumn",
                table: "MappingFields",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceLookupTable",
                table: "MappingFields",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceLookupKeyColumn",
                table: "MappingFields");

            migrationBuilder.DropColumn(
                name: "ReferenceLookupTable",
                table: "MappingFields");
        }
    }
}
