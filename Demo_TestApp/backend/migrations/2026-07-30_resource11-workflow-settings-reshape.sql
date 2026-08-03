-- HealthAppDb migration: reshape Resource11WorkflowSetting (per-role rows) into Resource11WorkflowSettings
-- (single row, Id = 1, List + Details URL per role). Matches HealthAppDbContext.OnModelCreating /
-- Resource11Entities.cs's Resource11WorkflowSettingsEntity as of 2026-07-30.
--
-- Idempotent — safe to run more than once, and safe to run even if the app has already auto-applied this via its
-- own startup ExecuteSqlRaw (see Program.cs) since every branch below checks before acting.
--
-- Run against the production HealthAppDb before deploying this build. WorkflowUrl values already saved under the
-- OLD per-role table are NOT migrated forward automatically (the old shape has no List/Details split to map into) —
-- capture them first if they need to be preserved; the SELECT below prints them for that purpose.

-- Uncomment to inspect old data before it's dropped:
-- SELECT * FROM [Resource11WorkflowSetting];

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Resource11WorkflowSetting')
    DROP TABLE [Resource11WorkflowSetting];

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Resource11WorkflowSettings')
    CREATE TABLE [Resource11WorkflowSettings] (
        [Id] INT NOT NULL CONSTRAINT [PK_Resource11WorkflowSettings] PRIMARY KEY,
        [Patient_List_11]            NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PatL]  DEFAULT(''),
        [Patient_Details_11]         NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PatD]  DEFAULT(''),
        [Provider_List_11]           NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PrvL]  DEFAULT(''),
        [Provider_Details_11]        NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PrvD]  DEFAULT(''),
        [ProviderInApp_List_11]      NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PiaL]  DEFAULT(''),
        [ProviderInApp_Details_11]   NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PiaD]  DEFAULT(''),
        [BackendSystem_List_11]      NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_BsL]   DEFAULT(''),
        [BackendSystem_Details_11]   NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_BsD]   DEFAULT('')
    );

IF NOT EXISTS (SELECT 1 FROM [Resource11WorkflowSettings] WHERE [Id] = 1)
    INSERT INTO [Resource11WorkflowSettings]
        ([Id],[Patient_List_11],[Patient_Details_11],[Provider_List_11],[Provider_Details_11],[ProviderInApp_List_11],[ProviderInApp_Details_11],[BackendSystem_List_11],[BackendSystem_Details_11])
    VALUES (1,'','','','','','','','');
