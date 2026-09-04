using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHapiLocalTerminologyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TRM_CODESYSTEM",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeSystemUri = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CsName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CurrentVersionPid = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CODESYSTEM", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CODESYSTEM_VER",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeSystemPid = table.Column<long>(type: "bigint", nullable: false),
                    CsVersionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CsDisplay = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CODESYSTEM_VER", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CONCEPT",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeSystemPid = table.Column<long>(type: "bigint", nullable: false),
                    CodeVal = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CONCEPT", x => x.Pid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CODESYSTEM_CodeSystemUri",
                schema: "terminology",
                table: "TRM_CODESYSTEM",
                column: "CodeSystemUri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CODESYSTEM_VER_CodeSystemPid_CsVersionId",
                schema: "terminology",
                table: "TRM_CODESYSTEM_VER",
                columns: new[] { "CodeSystemPid", "CsVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CONCEPT_CodeSystemPid_CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                columns: new[] { "CodeSystemPid", "CodeVal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TRM_CODESYSTEM",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CODESYSTEM_VER",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CONCEPT",
                schema: "terminology");
        }
    }
}
