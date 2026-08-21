using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransformationRuleSourceField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules");

            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules");

            migrationBuilder.AddColumn<string>(
                name: "SourceField",
                table: "TransformationRules",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem_SourceField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourcePipelineRouteId", "ResourceType", "DestinationField", "SourceSystem", "SourceField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem_SourceField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourceType", "DestinationField", "SourceSystem", "SourceField" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem_SourceField",
                table: "TransformationRules");

            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem_SourceField",
                table: "TransformationRules");

            migrationBuilder.DropColumn(
                name: "SourceField",
                table: "TransformationRules");

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourcePipelineRouteId", "ResourceType", "DestinationField", "SourceSystem" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourceType", "DestinationField", "SourceSystem" });
        }
    }
}
