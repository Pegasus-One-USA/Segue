-- Full dev-DB reset of workflow / pipeline / connection data.
-- Wipes: Runtime-plane workflows + their run history, Configured-Pipeline routes,
-- mapping profiles, source connections, destination configurations, webhook configs,
-- source capability-statement cache, configured-pipeline run history, and SMART
-- launch logs. Also removes any Key Vault secret rows left orphaned by the deleted
-- source/destination connections.
--
-- Deliberately KEPT (platform/reference data, not workflow/connection-scoped):
-- Users, UserRoles, Roles, Permissions, PermissionCategories, PermissionGroups,
-- PermissionAllocations, SystemSettings, NotificationSettings, EhrEndpoints,
-- AllowedCorsOrigins, and general Governance/audit logs (AuditLogs, SecurityEvents,
-- AuthenticationLogs, etc. — see the optional section at the bottom if you also want
-- those gone).
--
-- Deletion order respects every real FK constraint in FHIRBridgeDbContext (see
-- FHIRBridgeDbContextModelSnapshot.cs). Several tables here are only loosely
-- correlated by plain Guid columns (no FK) — order among those doesn't matter,
-- but they're still placed correctly relative to the FK-constrained ones.
--
-- No API restart needed; run against a stopped or idle instance to avoid deleting
-- rows out from under an in-flight pipeline/workflow run.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;   -- sqlcmd defaults this OFF, which breaks deletes against
                            -- tables with filtered/unique indexes (e.g. ProvisionedSecrets,
                            -- SourceCapabilityProfiles).

BEGIN TRANSACTION;

BEGIN TRY

    -- Capture Key Vault secret references belonging to the connections we're about
    -- to delete, so we can clean up ProvisionedSecrets afterward without guessing.
    CREATE TABLE #SecretRefs (KeyVaultName NVARCHAR(200) NOT NULL, SecretName NVARCHAR(200) NOT NULL);

    INSERT INTO #SecretRefs (KeyVaultName, SecretName)
    SELECT ClientSecretKeyVaultName, ClientSecretName FROM [SourceConnections] WHERE ClientSecretName IS NOT NULL
    UNION
    SELECT PrivateKeyKeyVaultName, PrivateKeySecretName FROM [SourceConnections] WHERE PrivateKeySecretName IS NOT NULL
    UNION
    SELECT KeyVaultName, SecretName FROM [DestinationConfigurations] WHERE SecretName IS NOT NULL;

    -- 1) Runtime-plane workflow execution history (no FK back to WorkflowDefinitions,
    --    so these are deleted by explicit table wipe rather than cascade).
    DELETE FROM [WorkflowNodeRunPayloads];
    DELETE FROM [WorkflowNodeRuns];
    DELETE FROM [WorkflowRuns];

    -- 2) Runtime-plane workflow graphs (WorkflowNodeConfigurations/WorkflowNodes/
    --    WorkflowEdges all cascade from WorkflowDefinitions, but deleting explicitly
    --    keeps this script correct even if cascade behavior ever changes).
    DELETE FROM [WorkflowNodeConfigurations];
    DELETE FROM [WorkflowNodes];
    DELETE FROM [WorkflowEdges];
    DELETE FROM [WorkflowDefinitions];

    -- 3) Configured-Pipeline routes (owned children first, Restrict FKs force this order).
    DELETE FROM [ResourcePipelineRouteMappingParentReferences];
    DELETE FROM [ResourcePipelineRouteMappings];
    DELETE FROM [ResourcePipelineRoutes];

    -- 4) Mapping profiles (must precede SourceConnections/SourceConfigurations).
    DELETE FROM [MappingFields];
    DELETE FROM [MappingProfiles];

    -- 5) Everything else hanging off a source connection.
    DELETE FROM [WebhookConfigurations];
    DELETE FROM [SourceCapabilityProfiles];
    DELETE FROM [SourceConfigurations];   -- must precede SourceConnections
    DELETE FROM [SourceConnections];

    -- 6) Destination connections (no FK dependents).
    DELETE FROM [DestinationConfigurations];

    -- 7) Execution/run history not tied to the Runtime plane.
    DELETE FROM [PipelineRunResourceRecords];
    DELETE FROM [PipelineRunRouteExecutions];
    DELETE FROM [ConfiguredPipelineRuns];

    -- 8) SMART/EHR launch diagnostics (loosely correlated to SourceConnections).
    DELETE FROM [SmartLaunchLogs];

    -- 9) Orphaned Key Vault secrets — skip any pair NotificationSettings still uses.
    DELETE ps
    FROM [ProvisionedSecrets] ps
    INNER JOIN #SecretRefs sr
        ON ps.KeyVaultName = sr.KeyVaultName AND ps.SecretName = sr.SecretName
    WHERE NOT EXISTS (
        SELECT 1 FROM [NotificationSettings] ns
        WHERE ns.PasswordKeyVaultName = ps.KeyVaultName AND ns.PasswordSecretName = ps.SecretName
    );

    DROP TABLE #SecretRefs;

    COMMIT TRANSACTION;

    PRINT 'Workflow/pipeline/connection data reset complete.';

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Sanity check: every row count below should be 0.
SELECT 'WorkflowDefinitions' AS TableName, COUNT(*) AS RemainingRows FROM [WorkflowDefinitions]
UNION ALL SELECT 'WorkflowRuns', COUNT(*) FROM [WorkflowRuns]
UNION ALL SELECT 'ResourcePipelineRoutes', COUNT(*) FROM [ResourcePipelineRoutes]
UNION ALL SELECT 'MappingProfiles', COUNT(*) FROM [MappingProfiles]
UNION ALL SELECT 'SourceConnections', COUNT(*) FROM [SourceConnections]
UNION ALL SELECT 'DestinationConfigurations', COUNT(*) FROM [DestinationConfigurations]
UNION ALL SELECT 'ConfiguredPipelineRuns', COUNT(*) FROM [ConfiguredPipelineRuns]
UNION ALL SELECT 'SmartLaunchLogs', COUNT(*) FROM [SmartLaunchLogs];

-- ---------------------------------------------------------------------------
-- Optional deeper wipe (uncomment to also clear general governance/audit logs —
-- these are NOT scoped to a specific workflow/connection, so they're excluded
-- by default; only uncomment if you want a truly clean slate):
--
-- DELETE FROM [AuditLogs];
-- DELETE FROM [DataAccessLogs];
-- DELETE FROM [AuthenticationLogs];
-- DELETE FROM [SecurityEvents];
-- DELETE FROM [AuthorizationLogs];
-- DELETE FROM [ArchiveManifestEntries];
-- DELETE FROM [AlertHistoryEntries];
-- DELETE FROM [SchedulerHistory];
-- DELETE FROM [RetryHistory];
-- DELETE FROM [ErrorLogs];
-- DELETE FROM [ErrorResolutions];
-- DELETE FROM [ApiRequestLogs];
-- DELETE FROM [ExportHistory];
-- DELETE FROM [NotificationHistory];
-- DELETE FROM [ValidationFailureLogs];
-- DELETE FROM [EndpointHealthChecks];
