using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// One-time data backfill for the WorkflowId column the previous migration (AddWorkflowIdToMappingProfile)
    /// added — every pre-existing MappingProfile has it null, and the application code only ever claims it
    /// lazily (MappingProfile.ClaimForWorkflow, on that profile's own workflow's NEXT save through
    /// /workflows/build). This migration instead resolves ownership right now, from data that already exists:
    /// each WorkflowNode's own embedded config (ConfigurationJson's "mappingProfileId"/"mappingProfileIds") is
    /// exactly what /workflows/build stamps onto a Mapping node pointing at the MappingProfile(s) it built for
    /// that workflow — see WorkflowEndpoints.cs's mapping-save loop and TransformNodeExecutors.ReadProfileIds.
    /// A profile referenced by more than one DISTINCT WorkflowDefinitionId (two workflows that, before WorkflowId
    /// existed, ended up sharing one profile for the same resourceType/source/destination triple) is
    /// deliberately left null rather than guessing an owner — claiming it for one would silently evict the
    /// other from ever finding it again. That single remaining case still resolves itself exactly as designed:
    /// whichever of the two workflows is next re-saved claims it then, and the other gets its own new profile.
    /// </summary>
    public partial class BackfillMappingProfileWorkflowId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ;WITH NodeProfileRefs AS (
                    -- The legacy single mappingProfileId (one resource per node, pre-dating multi-resource
                    -- destinations) — see TransformNodeExecutors.ReadProfileIds' own fallback for this same field.
                    SELECT
                        wn.WorkflowDefinitionId,
                        TRY_CAST(JSON_VALUE(wn.ConfigurationJson, '$.mappingProfileId') AS UNIQUEIDENTIFIER) AS ProfileId
                    FROM WorkflowNodes wn
                    WHERE JSON_VALUE(wn.ConfigurationJson, '$.mappingProfileId') IS NOT NULL

                    UNION

                    -- The current { resourceType: mappingProfileId } map WorkflowEndpoints.cs stamps when a
                    -- destination selects more than one resource — every value in it is a profile id.
                    SELECT
                        wn.WorkflowDefinitionId,
                        TRY_CAST(j.[value] AS UNIQUEIDENTIFIER) AS ProfileId
                    FROM WorkflowNodes wn
                    CROSS APPLY OPENJSON(JSON_QUERY(wn.ConfigurationJson, '$.mappingProfileIds')) j
                    WHERE JSON_QUERY(wn.ConfigurationJson, '$.mappingProfileIds') IS NOT NULL
                ),
                DistinctRefs AS (
                    SELECT DISTINCT WorkflowDefinitionId, ProfileId
                    FROM NodeProfileRefs
                    WHERE ProfileId IS NOT NULL
                ),
                UnambiguousProfiles AS (
                    -- MIN() is arbitrary but moot here — the HAVING clause already guarantees exactly one
                    -- distinct WorkflowDefinitionId survives per ProfileId, so MIN just extracts that one value.
                    SELECT ProfileId, MIN(WorkflowDefinitionId) AS WorkflowDefinitionId
                    FROM DistinctRefs
                    GROUP BY ProfileId
                    HAVING COUNT(DISTINCT WorkflowDefinitionId) = 1
                )
                UPDATE mp
                SET mp.WorkflowId = up.WorkflowDefinitionId
                FROM MappingProfiles mp
                JOIN UnambiguousProfiles up ON up.ProfileId = mp.Id
                WHERE mp.WorkflowId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort undo: there's no record of which rows this migration itself touched vs. which were
            // already claimed by the (independent, ongoing) lazy ClaimForWorkflow path by the time Down runs —
            // unclaiming everything is the closest faithful reversal of "this migration claimed some rows".
            migrationBuilder.Sql("UPDATE MappingProfiles SET WorkflowId = NULL;");
        }
    }
}
