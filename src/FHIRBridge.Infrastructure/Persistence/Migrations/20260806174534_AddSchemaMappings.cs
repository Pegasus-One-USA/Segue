using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SchemaMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DestinationTable = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DestinationField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaMappings_SourceSystem_ResourceType_DestinationTable_DestinationField",
                table: "SchemaMappings",
                columns: new[] { "SourceSystem", "ResourceType", "DestinationTable", "DestinationField" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaMappings");
        }
    }
}
