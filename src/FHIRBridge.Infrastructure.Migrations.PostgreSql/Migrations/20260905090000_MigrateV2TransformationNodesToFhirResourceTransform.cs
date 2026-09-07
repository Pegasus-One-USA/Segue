using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <summary>
    /// Repoints already-saved V2 workflows from the shared <c>NormalizationNode</c> onto V2's own
    /// <c>FhirResourceTransformNode</c>.
    ///
    /// V2's "Transformation" chain step originally persisted as NormalizationNode — the same node type V1's
    /// "Normalize Data" step uses. It now has its own type so the FHIR-resource rule engine it will carry
    /// cannot execute inside a V1 pipeline. Without this backfill the canvas would still render correctly
    /// (WorkflowGraphMapperServiceV2.nodeFromDto reads __transformId ahead of nodeType), but the PERSISTED
    /// graph would keep running the old passthrough executor — so those workflows would silently apply no
    /// transformations until someone happened to re-save them.
    ///
    /// Rank moves with it: the catalog places FhirResourceTransformNode at 34 (Normalization is 30), and
    /// WorkflowGraphValidator rejects any node whose rank disagrees with its catalog entry. 34 keeps the
    /// chain strictly increasing — Transformation 34 -> De-identification 50 -> Mapping 60 -> Destination 70.
    ///
    /// Scoped by BOTH markers written into ConfigurationJson by WorkflowGraphMapperServiceV2.nodeToRequest:
    /// __builderVersion "v2" and __transformId "transformation". A V1-authored NormalizationNode carries
    /// neither and is left untouched.
    /// </summary>
    public partial class MigrateV2TransformationNodesToFhirResourceTransform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowNodes"
                SET "NodeType" = 'FhirResourceTransformNode', "Rank" = 34
                WHERE "NodeType" = 'NormalizationNode'
                  AND "ConfigurationJson" LIKE '%"__builderVersion":"v2"%'
                  AND "ConfigurationJson" LIKE '%"__transformId":"transformation"%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "WorkflowNodes"
                SET "NodeType" = 'NormalizationNode', "Rank" = 30
                WHERE "NodeType" = 'FhirResourceTransformNode';
                """);
        }
    }
}
