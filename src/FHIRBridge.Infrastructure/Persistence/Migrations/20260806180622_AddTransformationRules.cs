using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransformationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransformationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DestinationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DestinationField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourcePipelineRouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NodeType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    OnNull = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorPolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_TransformationRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_DestinationType_DestinationField",
                table: "TransformationRules",
                columns: new[] { "Scope", "DestinationType", "DestinationField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourcePipelineRouteId", "ResourceType", "DestinationField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourceType", "DestinationField" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransformationRules");
        }
    }
}
