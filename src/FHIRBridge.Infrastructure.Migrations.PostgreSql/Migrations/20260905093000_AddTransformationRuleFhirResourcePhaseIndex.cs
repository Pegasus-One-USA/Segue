using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <summary>
    /// Index backing FhirResourceRuleResolver's per-tier lookups. FHIR-resource rules key on SourceField (the
    /// FHIR path the rule reads) rather than DestinationField, so none of the existing TransformationRules
    /// indexes — all of which lead with Scope + DestinationField — help; without this each tier walk scans.
    /// </summary>
    public partial class AddTransformationRuleFhirResourcePhaseIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_ExecutionPhase_Scope_ResourceType_SourceField",
                table: "TransformationRules",
                columns: new[] { "ExecutionPhase", "Scope", "ResourceType", "SourceField" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransformationRules_ExecutionPhase_Scope_ResourceType_SourceField",
                table: "TransformationRules");
        }
    }
}
