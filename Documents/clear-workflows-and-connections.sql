-- Clears the Workflow Designer (definitions + graph + run history + audit log), the ad-hoc Runtime
-- pipeline execution log, all Source Connections plus everything with a foreign key back to a
-- SourceConnection, and all Destination Configurations. Uses the same disable-constraints / delete /
-- re-enable-constraints technique as reset-to-minimum.sql so the tables below don't need to be listed
-- in strict dependency order.
--
-- Enforced (Restrict) FK chain feeding into SourceConnections, all of which must be cleared together
-- or the final WITH CHECK CHECK CONSTRAINT ALL re-validation below will fail on leftover rows:
--   ResourcePipelineRoutes --Restrict--> MappingProfiles, WebhookConfigurations
--   MappingProfiles        --Restrict--> SourceConnections, SourceConfigurations
--   SourceConfigurations   --Restrict--> SourceConnections
-- BulkExportJobs, SourceCapabilityProfiles, SmartLaunchLogs, and UserFhirContextBindings all carry a
-- SourceConnectionId column too, but it isn't a DB-enforced FK on any of them — they're included here
-- for referential cleanliness, not because leaving them would break the constraint re-check.
--
-- MappingProfile and ResourcePipelineRoute each own EF child collections stored in their own physical
-- tables (OwnsMany), which is easy to miss since they don't show up in FHIRBridgeDbContext's DbSet list:
--   MappingProfiles        owns MappingFields (FK MappingProfileId)
--   ResourcePipelineRoutes owns ResourcePipelineRouteMappings (FK ResourcePipelineRouteId, and a second
--                          Restrict FK MappingProfileId), which itself owns
--                          ResourcePipelineRouteMappingParentReferences (FK ResourcePipelineRouteMappingId)
-- All three must be cleared alongside their owners for the same reason as above.
--
-- DestinationConfigurations has no enforced FK from anything (MappingProfiles.DestinationId is a plain
-- indexed column, not a DB-level FK) and its own SecretReference is OwnsOne — same-table columns, no
-- separate child table — so it's a single-table addition with no ordering or orphan concerns.
--
-- NOT included: ConfiguredPipelineRuns/PipelineRunRouteExecutions/PipelineRunResourceRecords/
-- TransformationRules/SchemaMappings/ProcessedMessages — these belong to the Configured Pipeline path
-- (tenant-config-driven, see CLAUDE.md) and have no enforced FK to anything below, so wiping workflows/
-- connections/destinations does not require touching them.

DECLARE @ClearTables TABLE (TableName SYSNAME);

INSERT INTO @ClearTables (TableName)
VALUES
    -- Workflow Designer: definitions + graph (children cascade from WorkflowDefinitions/WorkflowNodes
    -- at the EF level, but we still list them explicitly since this is raw SQL, not EF SaveChanges).
    ('WorkflowDefinitions'), ('WorkflowNodes'), ('WorkflowNodeConfigurations'), ('WorkflowEdges'),

    -- Workflow Designer: run history + audit log (/api/v1/workflows execution engine).
    ('WorkflowRuns'), ('WorkflowNodeRuns'), ('WorkflowNodeRunPayloads'),
    ('FieldLineageEntries'), ('WorkflowAuditLogs'),

    -- Ad-hoc Runtime Plane pipeline execution log (SqlPipelineRunStore) — a separate history table from
    -- WorkflowRuns above, not tied to any WorkflowDefinition.
    ('PipelineRuns'), ('PipelineRunSteps'), ('PipelineRunEvents'),

    -- Source connections and everything with a column pointing back at one. Order matters only if you
    -- remove the NOCHECK/WITH CHECK dance below; with it, delete order is irrelevant.
    ('ResourcePipelineRouteMappingParentReferences'), ('ResourcePipelineRouteMappings'),
    ('ResourcePipelineRoutes'), ('MappingFields'), ('MappingProfiles'), ('SourceConfigurations'),
    ('WebhookConfigurations'), ('BulkExportJobs'), ('SourceCapabilityProfiles'),
    ('SmartLaunchLogs'), ('UserFhirContextBindings'), ('SourceConnections'),

    -- Destination configurations. No enforced FK points at this table, so it stands alone.
    ('DestinationConfigurations');

DECLARE @SQL NVARCHAR(MAX) = '';

-- Disable constraints
SELECT @SQL +=
'ALTER TABLE [' + s.name + '].[' + t.name + '] NOCHECK CONSTRAINT ALL;
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id;

EXEC sp_executesql @SQL;

SET @SQL = '';

-- Delete data for the listed tables only
SELECT @SQL +=
'DELETE FROM [' + s.name + '].[' + t.name + '];
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.name IN (SELECT TableName FROM @ClearTables);

EXEC sp_executesql @SQL;

SET @SQL = '';

-- Re-enable constraints
SELECT @SQL +=
'ALTER TABLE [' + s.name + '].[' + t.name + '] WITH CHECK CHECK CONSTRAINT ALL;
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id;

EXEC sp_executesql @SQL;

-- Verify nothing was left NOCHECK/untrusted — if the re-enable pass above hit a leftover-row conflict
-- on a table missing from @ClearTables (as MappingFields/ResourcePipelineRouteMappings/
-- ResourcePipelineRouteMappingParentReferences did once), that FK stays untrusted and shows up here.
-- A non-empty result means: add the missing table to @ClearTables, delete its orphaned rows, then
-- re-run just this re-enable block.
SELECT
    OBJECT_SCHEMA_NAME(parent_object_id) AS SchemaName,
    OBJECT_NAME(parent_object_id) AS TableName,
    name AS ConstraintName,
    is_disabled,
    is_not_trusted
FROM sys.foreign_keys
WHERE is_disabled = 1 OR is_not_trusted = 1;
