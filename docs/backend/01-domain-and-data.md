# 01 — Domain & Data

> Part of the [Backend Architecture Guide](README.md). Reviewed 2026-07-01 (`port/stepbase-overlay`).

## Domain model (`src/FHIRBridge.Domain`)

### Root aggregate: `Tenant`
`Aggregates/Tenant.cs` — a customer organization and multi-tenant isolation boundary. Owns the following, with **cascading enable/disable** (disabling a parent disables dependent children):

| Entity | Represents |
|---|---|
| `SourceConnection` | A source EHR/system. `SourceSystemType` enum (Epic, Cerner, GenericFhir, Healow, MeditechGreenfield, …). Owns auth config + optional interactive/OAuth config. Nullable `ApplicationType` (SMART app axis; null = legacy vendor-inferred). |
| `WebhookConfiguration` | A push endpoint for one source + resource type. |
| `DestinationConfiguration` | Where to write output. `DestinationType` enum (~21 targets). Owns a `SecretReference` → Key Vault. |
| `MappingProfile` | **Source of truth** for `{ResourceType, SourceConnectionId, DestinationId}`. Owns ordered `MappingField` value objects (JsonPath → TargetField, ValueType, ArrayPolicy, normalization, terminology). |
| `ResourcePipelineRoute` | How/when to run a mapping: `IngestionMode` (Webhook / ScheduledPull / Both), cron, search params, priority. References a `MappingProfile`. |

### RBAC entities
`User`, `Role`, `Permission`, `RolePermission`, `UserRole`, `TenantUser` (user↔tenant↔role join).
- 5 seeded platform roles: **GlobalAdmin, TenantAdmin, PipelineEngineer, Analyst, Auditor**.
- 28 permission codes in `Application/Security/UnifiedPermissions.cs`.
- Deterministic seed GUIDs in `SeededSecurityIds`; role→permission map in `UnifiedRolePermissionSeed`.

### Records & audit
| Entity | Purpose |
|---|---|
| `ConfiguredPipelineRunRecord` | Run metrics: extracted/mapped/written counts, status, errors, trigger type. |
| `SourceCapabilityProfile` | Cached FHIR CapabilityStatement; gates which resource types a mapping may target. |
| `OperationalAuditLog` | Append-only system/pipeline events (PHI-free). |
| `UserActivityAuditLog` | Append-only, **SHA-256 hash-chained tamper-evident** per-tenant chain (`PreviousHash` + `EntryHash` via `SealChain`). HIPAA §164.312. |
| `ResourceLineageEntry` | PHI-free chain-of-custody; purgeable per retention. |
| `ProcessedMessage` | Idempotency key store. |

### Value objects
`MappingField`, `SourceAuthenticationConfiguration`, `SourceInteractiveConfiguration`, `SecretReference` (KeyVault name + secret name; never stores the credential), `CapabilityResource`.

## Persistence (`src/FHIRBridge.Infrastructure/Persistence`)

**`FHIRBridgeDbContext`** — 18 DbSets. Conventions applied in `OnModelCreating`:
- **Soft-delete** global query filter for `ISoftDeletable` (deleted rows hidden, never physically removed).
- **`RowVersion`** → optimistic concurrency token.
- **Append-only audit guard** in `SaveChanges`/`SaveChangesAsync` — throws if an `OperationalAuditLog`/`UserActivityAuditLog` row is modified or deleted.

**`AuditingSaveChangesInterceptor`** stamps `CreatedOnUtc`/`CreatedBy` + `ModifiedOnUtc`/`ModifiedBy`, and converts hard deletes of `ISoftDeletable` into soft deletes. Actor comes from `ICurrentUserService` (defaults to "System" off-HTTP).

**Repositories** have EF Core + InMemory implementations, selected by connection-string presence in `DependencyInjection.cs`:
`ITenantConfigurationRepository`, `IUserAccessRepository`, `IConfiguredPipelineRunRepository`, `ISourceCapabilityRepository`, plus `IProcessedMessageStore` and lineage store.

**Migrations** (`Persistence/Migrations`, in order):
`20260617131626_InitialCreate` → `AddSourceCapabilityProfiles` → `20260701112851_AddSourceApplicationType` → `AddPatientSelectionMethod` → `20260701125943_AddRbac`.
Design-time factory `FHIRBridgeDbContextDesignTimeFactory` uses `FHIRBRIDGE_DB` env var, else `Server=localhost,1433;Database=FHIRBridge`. Add migrations with output dir `Persistence/Migrations`.

## Security infrastructure (`src/FHIRBridge.Infrastructure/Security`)
- `Pbkdf2PasswordHasher` — PBKDF2-SHA256, 350k iterations, self-contained versioned format `v1:iters:salt:hash`.
- `LocalIdentitySeedService` — bootstraps an admin user from `LocalAuth:SeedAdmin:*` config on startup.
- Secrets are never inline: `SecretReference` → Azure Key Vault (composite config + KeyVault provider in Infrastructure).
