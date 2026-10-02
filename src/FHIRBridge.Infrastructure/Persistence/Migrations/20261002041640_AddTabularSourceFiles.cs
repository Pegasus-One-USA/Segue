using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTabularSourceFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TabularSourceFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    EncryptedContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TabularSourceFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TabularSourceFiles_CreatedOnUtc",
                table: "TabularSourceFiles",
                column: "CreatedOnUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TabularSourceFiles");
        }
    }
}
