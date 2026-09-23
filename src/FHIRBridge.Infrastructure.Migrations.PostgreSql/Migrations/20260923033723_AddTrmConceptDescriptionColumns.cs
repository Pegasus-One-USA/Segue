using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddTrmConceptDescriptionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "terminology",
                table: "TRM_CONCEPT",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "LongCommonName",
                schema: "terminology",
                table: "TRM_CONCEPT",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LongDescription",
                schema: "terminology",
                table: "TRM_CONCEPT",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortDescription",
                schema: "terminology",
                table: "TRM_CONCEPT",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "terminology",
                table: "TRM_CONCEPT");

            migrationBuilder.DropColumn(
                name: "LongCommonName",
                schema: "terminology",
                table: "TRM_CONCEPT");

            migrationBuilder.DropColumn(
                name: "LongDescription",
                schema: "terminology",
                table: "TRM_CONCEPT");

            migrationBuilder.DropColumn(
                name: "ShortDescription",
                schema: "terminology",
                table: "TRM_CONCEPT");
        }
    }
}
