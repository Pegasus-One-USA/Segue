using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeTrmConceptCodeValCaseSensitive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                collation: "SQL_Latin1_General_CP1_CS_AS",
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500,
                oldCollation: "SQL_Latin1_General_CP1_CS_AS");
        }
    }
}
