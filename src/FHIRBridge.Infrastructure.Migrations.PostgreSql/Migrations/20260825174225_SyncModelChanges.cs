using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelChanges : Migration
    {
        // The well-known Default Tenant id — must match SeededSecurityIds.DefaultTenantId exactly, and the
        // SqlServer AddTenantAndScopeUsersAndBranding migration's DefaultTenantId. Every pre-existing Users
        // row is assigned to this tenant so migrating an existing database changes zero observable behavior.
        private static readonly Guid DefaultTenantId = new("20000000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        // Every operation below is written as guarded raw SQL (IF NOT EXISTS / IF EXISTS / ON CONFLICT / a
        // pg_constraint existence check for the FK) so this migration is safe to re-apply against a database
        // where some or all of these objects already exist — see the AddHapiLocalTerminologyAndImportHistory
        // migration for why: EF's own __EFMigrationsHistory table already prevents a *recorded* migration
        // from re-running, but this project's Postgres migration history has previously contained a
        // duplicate migration (SyncModelToLatest, deleted) that re-issued these exact statements and failed
        // with "column already exists" — these guards make that class of mistake a no-op instead of a crash.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RefreshTokenRememberMe" boolean NOT NULL DEFAULT TRUE;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "TransformationRules" ADD COLUMN IF NOT EXISTS "ExpectedValueType" character varying(20);
                """);

            // 1) Create Tenants first and seed the Default Tenant row BEFORE Users.TenantId's FK can be
            // added — Users.TenantId's default value below must reference an already-inserted, valid
            // Tenant id (not Guid.Empty, which would violate the FK the moment it's checked against the
            // existing rows backfilled by that same ADD COLUMN statement).
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "Tenants" (
                    "Id" uuid NOT NULL,
                    "Name" character varying(200) NOT NULL,
                    "Code" character varying(50) NOT NULL,
                    "IsActive" boolean NOT NULL,
                    "CreatedOnUtc" timestamp with time zone NOT NULL DEFAULT (timezone('utc', now())),
                    "CreatedBy" character varying(320) NOT NULL DEFAULT 'system',
                    "ModifiedOnUtc" timestamp with time zone NULL,
                    "ModifiedBy" text NULL,
                    "IsDeleted" boolean NOT NULL,
                    "DeletedOnUtc" timestamp with time zone NULL,
                    "DeletedBy" text NULL,
                    "RowVersion" bytea NOT NULL,
                    CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id")
                );
                """);

            // RowVersion has no store-generated value on Npgsql (see FHIRBridgeDbContext.OnModelCreating's
            // Npgsql branch / AuditingSaveChangesInterceptor.Stamp), so a raw insert must supply one
            // explicitly or the NOT NULL constraint fails. uuid_send(...) gives a stable 16-byte value —
            // RowVersion is an opaque concurrency token, never compared against a Guid's own byte layout.
            migrationBuilder.Sql(
                $"""
                INSERT INTO "Tenants" ("Id", "Name", "Code", "IsActive", "CreatedBy", "IsDeleted", "RowVersion")
                VALUES (
                    '{DefaultTenantId}',
                    'Default Tenant',
                    'default',
                    TRUE,
                    'system',
                    FALSE,
                    uuid_send('{DefaultTenantId}'::uuid)
                )
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.Sql(
                $"""
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "TenantId" uuid NOT NULL DEFAULT '{DefaultTenantId}';
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "BrandConfigurations" (
                    "Id" uuid NOT NULL,
                    "TenantId" uuid NOT NULL,
                    "CompanyName" character varying(200) NOT NULL,
                    "PrimaryColor" character varying(7) NOT NULL,
                    "SecondaryColor" character varying(7) NOT NULL,
                    "AccentColor" character varying(7) NOT NULL,
                    "BackgroundColor" character varying(7) NOT NULL,
                    "FontFamily" character varying(200) NOT NULL,
                    "FooterText" character varying(500) NOT NULL,
                    "SupportEmail" character varying(255) NOT NULL,
                    "SupportPhone" character varying(50) NOT NULL,
                    "Website" character varying(500) NOT NULL,
                    "EmailFooterText" character varying(500) NOT NULL,
                    "DefaultThemeMode" character varying(20) NOT NULL,
                    "LoaderStyle" character varying(20) NOT NULL,
                    "LogoUrl" text NOT NULL,
                    "DarkLogoUrl" text NOT NULL,
                    "FaviconUrl" text NOT NULL,
                    "LoginBackgroundUrl" text NOT NULL,
                    "LoginIllustrationUrl" text NOT NULL,
                    "EmailLogoUrl" text NOT NULL,
                    "CreatedOnUtc" timestamp with time zone NOT NULL DEFAULT (timezone('utc', now())),
                    "CreatedBy" character varying(320) NOT NULL DEFAULT 'system',
                    "ModifiedOnUtc" timestamp with time zone NULL,
                    "ModifiedBy" text NULL,
                    "IsDeleted" boolean NOT NULL,
                    "DeletedOnUtc" timestamp with time zone NULL,
                    "DeletedBy" text NULL,
                    "RowVersion" bytea NOT NULL,
                    CONSTRAINT "PK_BrandConfigurations" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_BrandConfigurations_Tenants_TenantId" FOREIGN KEY ("TenantId")
                        REFERENCES "Tenants" ("Id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Users_TenantId" ON "Users" ("TenantId");
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_BrandConfigurations_TenantId" ON "BrandConfigurations" ("TenantId");
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tenants_Code" ON "Tenants" ("Code");
                """);

            // Postgres has no "ADD CONSTRAINT IF NOT EXISTS" — guard via pg_constraint instead.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Users_Tenants_TenantId') THEN
                        ALTER TABLE "Users" ADD CONSTRAINT "FK_Users_Tenants_TenantId"
                            FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users" DROP CONSTRAINT IF EXISTS "FK_Users_Tenants_TenantId";
                """);

            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "BrandConfigurations";
                """);

            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "Tenants";
                """);

            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Users_TenantId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Users" DROP COLUMN IF EXISTS "RefreshTokenRememberMe";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Users" DROP COLUMN IF EXISTS "TenantId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "TransformationRules" DROP COLUMN IF EXISTS "ExpectedValueType";
                """);
        }
    }
}
