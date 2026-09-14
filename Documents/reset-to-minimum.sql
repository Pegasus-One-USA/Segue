-- Tables to keep
DECLARE @KeepTables TABLE (TableName SYSNAME);

INSERT INTO @KeepTables (TableName)
VALUES
    ('__EFMigrationsHistory'),

    -- Default Tenant row is a one-time data migration INSERT, not reseeded by Database.Migrate().
    -- SetupService hardcodes SeededSecurityIds.DefaultTenantId for the first-run SuperAdmin — if this
    -- table is empty, first-run signup fails with an FK violation. Keeping the whole table also keeps
    -- any other tenants you've created; delete extra rows manually afterward if you want a single tenant.
    ('Tenants'),

    -- Terminology reference data: loaded via long-running import jobs against external feeds
    -- (LOINC/SNOMED/ICD-10/ICD-10-PCS/RxNorm/NDC/HCPCS/CVX/UCUM). Nothing in the app reseeds these —
    -- wiping them means re-running the full terminology import pipeline.
    ('LoincConcepts'), ('LoincParts'), ('LoincGroups'), ('LoincAnswerLists'),
    ('LoincConceptMaps'), ('LoincVersions'), ('LoincImportHistory'),

    ('SnomedConcepts'), ('SnomedDescriptions'), ('SnomedRelationships'),
    ('SnomedVersions'), ('SnomedImportHistory'),

    ('Icd10Codes'), ('Icd10Versions'), ('Icd10ImportHistory'),
    ('Icd10PcsCodes'), ('Icd10PcsVersions'), ('Icd10PcsImportHistory'),

    ('RxNormConcepts'), ('RxNormVersions'), ('RxNormImportHistory'),

    ('NdcProducts'), ('NdcVersions'), ('NdcImportHistory'),

    ('HcpcsCodes'), ('HcpcsVersions'), ('HcpcsImportHistory'),

    ('CvxCodes'), ('CvxVersions'), ('CvxImportHistory'),

    ('UcumUnits'), ('UcumVersions'), ('UcumImportHistory'),

    -- RBAC taxonomy. RbacBootstrapper (Program.cs, on API startup) tops these up if missing, but that
    -- only helps once the process actually restarts — keeping them means the portal's permission
    -- checks never see a gap, restart or not. Deliberately NOT keeping PermissionAllocations: it has
    -- real FKs to both Users and Roles/Permissions (PermissionAllocationConfiguration.cs), and Users
    -- is being wiped — any kept allocation row tied to a deleted user would fail the final
    -- WITH CHECK CHECK CONSTRAINT ALL re-validation below. Leaving it wiped keeps it empty (trivially
    -- FK-valid); RbacBootstrapper re-adds the role-level grants on next restart.
    ('PermissionCategories'), ('PermissionGroups'), ('Permissions'), ('Roles'),

    -- No seeder exists for this table (unlike the four above) — it is never reconstructed on restart.
    -- The effective CORS allow-list is Portal:AllowedOrigins (appsettings floor) UNION this table's
    -- rows (Program.cs:236-238). A deployed custom domain added via the admin screen — e.g.
    -- segue.pegasusone.com — likely lives ONLY here, not in the appsettings floor; wiping it can drop
    -- that origin from the allow-list and break every browser call against that domain.
    ('AllowedCorsOrigins');

-- Everything NOT in @KeepTables gets wiped, including Users/PermissionAllocations/SystemSettings/
-- EhrEndpoints/DeIdentificationProfiles/ProvisionedSecrets/BrandConfigurations. Restart the API
-- afterward regardless: Database.Migrate() + the three remaining startup seeders
-- (EhrEndpointDirectorySeeder, SystemSettingsSeeder, DeIdentificationProfileSeeder) repopulate those.
-- Users is deliberately NOT reseeded — that's what puts the portal back on the first-run signup page
-- (SetupService.RequiresSetupAsync == Users.Count == 0). BrandConfigurations has no seeder at all;
-- it just falls back to built-in default branding until someone saves branding again.

DECLARE @SQL NVARCHAR(MAX) = '';

-- Disable constraints
SELECT @SQL +=
'ALTER TABLE [' + s.name + '].[' + t.name + '] NOCHECK CONSTRAINT ALL;
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id;

EXEC sp_executesql @SQL;

SET @SQL = '';

-- Delete data except kept tables
SELECT @SQL +=
'DELETE FROM [' + s.name + '].[' + t.name + '];
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.name NOT IN (SELECT TableName FROM @KeepTables);

EXEC sp_executesql @SQL;

SET @SQL = '';

-- Re-enable constraints
SELECT @SQL +=
'ALTER TABLE [' + s.name + '].[' + t.name + '] WITH CHECK CHECK CONSTRAINT ALL;
'
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id;

EXEC sp_executesql @SQL;
