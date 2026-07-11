using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Every step below is guarded with an existence/type check before acting. This migration
    /// squashes 9 previously-separate migrations from the RBAC+MFA feature branch; guarding each
    /// step lets it run as a no-op (and still record itself in __EFMigrationsHistory) against any
    /// database that already reached the same final schema via the old, now-deleted migrations,
    /// instead of failing with "column/table already exists".
    ///
    /// Statements that reference a column which may not exist yet (ALTER COLUMN / UPDATE / DROP
    /// COLUMN against a column only conditionally present) are wrapped in EXEC(N'...') dynamic SQL.
    /// SQL Server compiles an entire batch up front — deferred name resolution covers missing
    /// *objects* referenced by name, but not a missing *column* referenced in DML/ALTER COLUMN/DROP
    /// COLUMN against a table that does exist. Without the EXEC(...) wrapper, a fresh database
    /// (where the column doesn't exist yet) fails to compile the "else, fix up the existing column"
    /// branch even though it would never run — the whole batch fails before any IF is evaluated.
    /// ADD COLUMN and CREATE INDEX/TABLE don't need this: they define a new object rather than
    /// binding to an existing one, so they compile regardless of runtime branch.
    /// </remarks>
    public partial class AddRbacPermissionHierarchyAndMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Permissions_PermissionCategories_CategoryId' AND parent_object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    ALTER TABLE [Permissions] DROP CONSTRAINT [FK_Permissions_PermissionCategories_CategoryId];
END");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_Name' AND object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    DROP INDEX [IX_Permissions_Name] ON [Permissions];
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'CategoryId') IS NOT NULL AND COL_LENGTH(N'Permissions', N'GroupId') IS NULL
BEGIN
    EXEC sp_rename N'[Permissions].[CategoryId]', N'GroupId', 'COLUMN';
END");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_CategoryId' AND object_id = OBJECT_ID(N'[Permissions]'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_GroupId' AND object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    EXEC sp_rename N'[Permissions].[IX_Permissions_CategoryId]', N'IX_Permissions_GroupId', 'INDEX';
END");

            // ── Users: MFA columns ──────────────────────────────────────────────
            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Users', N'MfaChallengeExpiresOnUtc') IS NULL
BEGIN
    ALTER TABLE [Users] ADD [MfaChallengeExpiresOnUtc] datetime2 NULL;
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Users' AND COLUMN_NAME = N'MfaChallengeExpiresOnUtc' AND DATA_TYPE <> N'datetime2'
)
BEGIN
    EXEC(N'ALTER TABLE [Users] ALTER COLUMN [MfaChallengeExpiresOnUtc] datetime2 NULL;');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Users', N'MfaChallengeTokenHash') IS NULL
BEGIN
    ALTER TABLE [Users] ADD [MfaChallengeTokenHash] nvarchar(500) NULL;
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Users' AND COLUMN_NAME = N'MfaChallengeTokenHash'
      AND (DATA_TYPE <> N'nvarchar' OR CHARACTER_MAXIMUM_LENGTH <> 500)
)
BEGIN
    EXEC(N'ALTER TABLE [Users] ALTER COLUMN [MfaChallengeTokenHash] nvarchar(500) NULL;');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Users', N'MustSetupMfa') IS NULL
BEGIN
    ALTER TABLE [Users] ADD [MustSetupMfa] bit NOT NULL DEFAULT CAST(0 AS bit);
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Users' AND COLUMN_NAME = N'MustSetupMfa'
      AND (DATA_TYPE <> N'bit' OR IS_NULLABLE = N'YES')
)
BEGIN
    EXEC(N'UPDATE [Users] SET [MustSetupMfa] = 0 WHERE [MustSetupMfa] IS NULL; ALTER TABLE [Users] ALTER COLUMN [MustSetupMfa] bit NOT NULL;');
END");

            // ── Permissions: taxonomy columns ───────────────────────────────────
            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'DisplayName') IS NULL
BEGIN
    ALTER TABLE [Permissions] ADD [DisplayName] nvarchar(150) NOT NULL DEFAULT N'';
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Permissions' AND COLUMN_NAME = N'DisplayName'
      AND (DATA_TYPE <> N'nvarchar' OR CHARACTER_MAXIMUM_LENGTH <> 150 OR IS_NULLABLE = N'YES')
)
BEGIN
    EXEC(N'UPDATE [Permissions] SET [DisplayName] = SPACE(0) WHERE [DisplayName] IS NULL; ALTER TABLE [Permissions] ALTER COLUMN [DisplayName] nvarchar(150) NOT NULL;');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'Instances') IS NULL
BEGIN
    ALTER TABLE [Permissions] ADD [Instances] nvarchar(1000) NULL;
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Permissions' AND COLUMN_NAME = N'Instances'
      AND (DATA_TYPE <> N'nvarchar' OR CHARACTER_MAXIMUM_LENGTH <> 1000)
)
BEGIN
    EXEC(N'ALTER TABLE [Permissions] ALTER COLUMN [Instances] nvarchar(1000) NULL;');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'IsActive') IS NULL
BEGIN
    ALTER TABLE [Permissions] ADD [IsActive] bit NOT NULL DEFAULT CAST(0 AS bit);
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Permissions' AND COLUMN_NAME = N'IsActive'
      AND (DATA_TYPE <> N'bit' OR IS_NULLABLE = N'YES')
)
BEGIN
    EXEC(N'UPDATE [Permissions] SET [IsActive] = 0 WHERE [IsActive] IS NULL; ALTER TABLE [Permissions] ALTER COLUMN [IsActive] bit NOT NULL;');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'IsVisible') IS NULL
BEGIN
    ALTER TABLE [Permissions] ADD [IsVisible] bit NOT NULL DEFAULT CAST(0 AS bit);
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'Permissions' AND COLUMN_NAME = N'IsVisible'
      AND (DATA_TYPE <> N'bit' OR IS_NULLABLE = N'YES')
)
BEGIN
    EXEC(N'UPDATE [Permissions] SET [IsVisible] = 0 WHERE [IsVisible] IS NULL; ALTER TABLE [Permissions] ALTER COLUMN [IsVisible] bit NOT NULL;');
END");

            // ── PermissionCategories: taxonomy columns ──────────────────────────
            migrationBuilder.Sql(@"
IF COL_LENGTH(N'PermissionCategories', N'DisplayName') IS NULL
BEGIN
    ALTER TABLE [PermissionCategories] ADD [DisplayName] nvarchar(100) NOT NULL DEFAULT N'';
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'PermissionCategories' AND COLUMN_NAME = N'DisplayName'
      AND (DATA_TYPE <> N'nvarchar' OR CHARACTER_MAXIMUM_LENGTH <> 100 OR IS_NULLABLE = N'YES')
)
BEGIN
    EXEC(N'UPDATE [PermissionCategories] SET [DisplayName] = SPACE(0) WHERE [DisplayName] IS NULL; ALTER TABLE [PermissionCategories] ALTER COLUMN [DisplayName] nvarchar(100) NOT NULL;');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'PermissionCategories', N'IsVisible') IS NULL
BEGIN
    ALTER TABLE [PermissionCategories] ADD [IsVisible] bit NOT NULL DEFAULT CAST(0 AS bit);
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = N'PermissionCategories' AND COLUMN_NAME = N'IsVisible'
      AND (DATA_TYPE <> N'bit' OR IS_NULLABLE = N'YES')
)
BEGIN
    EXEC(N'UPDATE [PermissionCategories] SET [IsVisible] = 0 WHERE [IsVisible] IS NULL; ALTER TABLE [PermissionCategories] ALTER COLUMN [IsVisible] bit NOT NULL;');
END");

            // ── New PermissionGroups table (table-level existence guard only —
            // a brand-new table has no prior-column-drift scenario to reconcile) ──
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[PermissionGroups]', N'U') IS NULL
BEGIN
    CREATE TABLE [PermissionGroups] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [DisplayName] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NULL,
        [CategoryId] uniqueidentifier NOT NULL,
        [IsVisible] bit NOT NULL,
        [CreatedOnUtc] datetime2 NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [ModifiedOnUtc] datetime2 NULL,
        [ModifiedBy] nvarchar(max) NULL,
        [IsDeleted] bit NOT NULL,
        [DeletedOnUtc] datetime2 NULL,
        [DeletedBy] nvarchar(max) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_PermissionGroups] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PermissionGroups_PermissionCategories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [PermissionCategories] ([Id]) ON DELETE NO ACTION
    );
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_MfaChallengeTokenHash' AND object_id = OBJECT_ID(N'[Users]'))
BEGIN
    CREATE INDEX [IX_Users_MfaChallengeTokenHash] ON [Users] ([MfaChallengeTokenHash]);
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_Name' AND object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    CREATE INDEX [IX_Permissions_Name] ON [Permissions] ([Name]);
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PermissionGroups_CategoryId' AND object_id = OBJECT_ID(N'[PermissionGroups]'))
BEGIN
    CREATE INDEX [IX_PermissionGroups_CategoryId] ON [PermissionGroups] ([CategoryId]);
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PermissionGroups_Name' AND object_id = OBJECT_ID(N'[PermissionGroups]'))
BEGIN
    CREATE UNIQUE INDEX [IX_PermissionGroups_Name] ON [PermissionGroups] ([Name]);
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Permissions_PermissionGroups_GroupId' AND parent_object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    ALTER TABLE [Permissions] ADD CONSTRAINT [FK_Permissions_PermissionGroups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [PermissionGroups] ([Id]) ON DELETE SET NULL;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Permissions_PermissionGroups_GroupId' AND parent_object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    ALTER TABLE [Permissions] DROP CONSTRAINT [FK_Permissions_PermissionGroups_GroupId];
END");

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[PermissionGroups]', N'U') IS NOT NULL
BEGIN
    DROP TABLE [PermissionGroups];
END");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_MfaChallengeTokenHash' AND object_id = OBJECT_ID(N'[Users]'))
BEGIN
    DROP INDEX [IX_Users_MfaChallengeTokenHash] ON [Users];
END");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_Name' AND object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    DROP INDEX [IX_Permissions_Name] ON [Permissions];
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Users', N'MfaChallengeExpiresOnUtc') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'MfaChallengeExpiresOnUtc');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Users] DROP COLUMN [MfaChallengeExpiresOnUtc];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Users', N'MfaChallengeTokenHash') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'MfaChallengeTokenHash');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Users] DROP COLUMN [MfaChallengeTokenHash];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Users', N'MustSetupMfa') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'MustSetupMfa');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Users] DROP COLUMN [MustSetupMfa];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'DisplayName') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Permissions]') AND [c].[name] = N'DisplayName');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Permissions] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Permissions] DROP COLUMN [DisplayName];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'Instances') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Permissions]') AND [c].[name] = N'Instances');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Permissions] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Permissions] DROP COLUMN [Instances];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'IsActive') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Permissions]') AND [c].[name] = N'IsActive');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Permissions] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Permissions] DROP COLUMN [IsActive];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'IsVisible') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Permissions]') AND [c].[name] = N'IsVisible');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Permissions] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [Permissions] DROP COLUMN [IsVisible];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'PermissionCategories', N'DisplayName') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[PermissionCategories]') AND [c].[name] = N'DisplayName');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [PermissionCategories] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [PermissionCategories] DROP COLUMN [DisplayName];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'PermissionCategories', N'IsVisible') IS NOT NULL
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[PermissionCategories]') AND [c].[name] = N'IsVisible');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [PermissionCategories] DROP CONSTRAINT [' + @var + '];');
    EXEC(N'ALTER TABLE [PermissionCategories] DROP COLUMN [IsVisible];');
END");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'Permissions', N'GroupId') IS NOT NULL AND COL_LENGTH(N'Permissions', N'CategoryId') IS NULL
BEGIN
    EXEC sp_rename N'[Permissions].[GroupId]', N'CategoryId', 'COLUMN';
END");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_GroupId' AND object_id = OBJECT_ID(N'[Permissions]'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_CategoryId' AND object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    EXEC sp_rename N'[Permissions].[IX_Permissions_GroupId]', N'IX_Permissions_CategoryId', 'INDEX';
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Permissions_Name' AND object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Permissions_Name] ON [Permissions] ([Name]);
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Permissions_PermissionCategories_CategoryId' AND parent_object_id = OBJECT_ID(N'[Permissions]'))
BEGIN
    ALTER TABLE [Permissions] ADD CONSTRAINT [FK_Permissions_PermissionCategories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [PermissionCategories] ([Id]) ON DELETE SET NULL;
END");
        }
    }
}
