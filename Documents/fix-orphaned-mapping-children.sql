-- Remediation for the partial run of clear-workflows-and-connections.sql: that run deleted
-- ResourcePipelineRoutes/MappingProfiles/SourceConnections etc. but missed three EF owned-collection
-- child tables (not in FHIRBridgeDbContext's DbSet list, so easy to miss), leaving them orphaned and
-- the WITH CHECK CHECK CONSTRAINT ALL re-enable pass failing on FK_MappingFields_MappingProfiles_MappingProfileId.
-- Delete child-of-child first (ResourcePipelineRouteMappingParentReferences references
-- ResourcePipelineRouteMappings, which references both ResourcePipelineRoutes and MappingProfiles).

DELETE FROM [dbo].[ResourcePipelineRouteMappingParentReferences];
DELETE FROM [dbo].[ResourcePipelineRouteMappings];
DELETE FROM [dbo].[MappingFields];

-- Re-run the re-enable pass for the whole DB (matches the tail of clear-workflows-and-connections.sql)
-- to clear the NOCHECK/untrusted state left behind by the interrupted run.
DECLARE @SQL NVARCHAR(MAX) = '';

SELECT @SQL +=
'ALTER TABLE [' + s.name + '].[' + t.name + '] WITH CHECK CHECK CONSTRAINT ALL;
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id;

EXEC sp_executesql @SQL;

-- Verify nothing is left NOCHECK/untrusted.
SELECT
    OBJECT_SCHEMA_NAME(parent_object_id) AS SchemaName,
    OBJECT_NAME(parent_object_id) AS TableName,
    name AS ConstraintName,
    is_disabled,
    is_not_trusted
FROM sys.foreign_keys
WHERE is_disabled = 1 OR is_not_trusted = 1;
