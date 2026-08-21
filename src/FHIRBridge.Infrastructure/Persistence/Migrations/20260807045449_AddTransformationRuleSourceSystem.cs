using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransformationRuleSourceSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField",
                table: "TransformationRules");

            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField",
                table: "TransformationRules");

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                table: "TransformationRules",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourcePipelineRouteId", "ResourceType", "DestinationField", "SourceSystem" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourceType", "DestinationField", "SourceSystem" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules");

            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem",
                table: "TransformationRules");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                table: "TransformationRules");

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourcePipelineRouteId", "ResourceType", "DestinationField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourceType", "DestinationField" });
        }
    }
}
