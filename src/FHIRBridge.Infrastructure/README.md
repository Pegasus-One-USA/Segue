# FHIRBridge.Infrastructure

> The concrete implementation layer — EF Core persistence, ~24 destination writers, governance/de-identification, terminology, messaging brokers, normalization, audit, and all the I/O the Application abstractions promise.

**Layer:** Infrastructure · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
`FHIRBridge.Infrastructure` is the outermost dependency-bearing ring of the Clean Architecture. It supplies the concrete classes behind every `FHIRBridge.Application.Abstractions.*` interface: databases, message brokers, secret stores, file/cloud destinations, terminology services, governance policies, and health checks. It is the only layer that knows about EF Core, SQL Server/PostgreSQL/MySQL, Azure Service Bus/RabbitMQ, Key Vault, QuestPDF, Parquet, SFTP, and the like. Nothing in Domain or Application references it; it is wired in at composition time via the single `AddFHIRBridgeInfrastructure` extension, which also chooses in-memory vs. durable implementations based on configuration so the platform runs end-to-end with or without a database.

## Responsibilities
- Provide the EF Core `DbContext`, entity configurations, migrations, repositories, and a `SaveChanges` interceptor that enforce persistence, soft-delete, optimistic concurrency, and HIPAA append-only audit rules.
- Implement the configured-pipeline destination writers (relational, file, cloud, BI, FHIR, REST) and a factory that resolves the writer for a `DestinationType`.
- Enforce data governance: per-resource-type access policy, FHIR consent, HIPAA Safe Harbor + Expert-Determination (k-anonymity) de-identification, configurable retention/purge, and lineage tracking.
- Drive the normalization pipeline (extension flattening, US Core validation, data-quality scoring, patient matching) and terminology services (lookup, translate, validate, expand) with local-then-remote-FHIR fallback and distributed caching.
- Move work asynchronously through a pluggable messaging transport (in-memory, RabbitMQ, Azure Service Bus) with idempotency, retry, and command handlers; ingest HL7 v2 messages over MLLP.
- Resolve secrets (Azure Key Vault + configuration fallback), hash passwords (PBKDF2), seed local identity, and surface the current user for audit stamping.
- Discover and test source connections, manage FHIR Subscriptions, evaluate schedules, aggregate patient-scoped reads, and expose health checks.
- Register everything — and pick in-memory vs. durable variants from configuration — in `DependencyInjection.AddFHIRBridgeInfrastructure`.

## Key components

### Persistence (`Persistence/`)
- **FHIRBridgeDbContext** — the EF Core context. Exposes `DbSet`s for tenants, source connections, webhooks, destinations, mapping profiles, pipeline routes, source-capability profiles, pipeline-run records, processed messages, and the RBAC entities (users, roles, permissions, role-permissions, user-roles, tenant-users). Applies all `IEntityTypeConfiguration` from the assembly, then layers cross-cutting conventions: a global soft-delete query filter for every `ISoftDeletable` and automatic `RowVersion` concurrency tokens.
- **AuditingSaveChangesInterceptor** — stamps created/modified provenance on `IAuditableEntity` rows and converts physical deletes of `ISoftDeletable` rows into soft deletes, using `ICurrentUserService` as the actor.
- **Configurations/** — one `IEntityTypeConfiguration` per entity (tenant, source connection, mapping profile, destination, pipeline route, processed message, RBAC tables) plus `SeedConstants` for deterministic seed ids.
- **Repositories** — EF + in-memory pairs for each aggregate: `Ef/InMemoryTenantConfigurationRepository`, `…UserAccessRepository`, `…ConfiguredPipelineRunRepository`, `…SourceCapabilityRepository`. The in-memory variants let the whole stack run with no database.
- **Migrations/** — `InitialCreate` and `AddSourceCapabilityProfiles`, plus the model snapshot.
- **FHIRBridgeDbContextDesignTimeFactory** — `IDesignTimeDbContextFactory` so `dotnet ef` can build the context outside the host.

### Destinations (`Destinations/`)
- **RelationalDestinationWriterBase** — provider-agnostic ADO.NET writer that auto-creates schema/table and writes mapped records in Insert or portable Upsert mode (delete-by-key + insert in a transaction); subclasses supply dialect hooks (connection, quoting, type mapping, DDL). Validates SQL identifiers and stores values as text for cross-dialect safety.
- **Relational writers** — `MappedSqlServerDestinationWriter` (also serves AzureSql), `MappedPostgreSqlDestinationWriter`, `MappedMySqlDestinationWriter`, `MappedSnowflakeDestinationWriter`, `MappedDatabricksDestinationWriter`.
- **File/serialization writers** — `MappedExcelDestinationWriter` (CSV + Excel), `MappedNdjsonDestinationWriter`, `MappedParquetDestinationWriter`, `MappedAvroDestinationWriter`, `MappedProtobufDestinationWriter`, `MappedPdfDestinationWriter` (QuestPDF), plus `MappedDestinationSerialization` helpers.
- **Cloud/transport writers** — `MappedBlobStorageDestinationWriter`, `MappedS3DestinationWriter`, `MappedSftpDestinationWriter` (SSH.NET).
- **API/BI/FHIR writers** — `MappedRestApiDestinationWriter`, `MappedFhirRepositoryDestinationWriter`, `MappedPowerBiDestinationWriter`, `MappedTableauDestinationWriter`.
- **In-memory** — `MappedInMemoryDestinationWriter` + `MappedInMemoryDestinationBuffer` for tests/dev.
- **ConfiguredDestinationWriterFactory** — resolves the writer for a `DestinationType` from injected `DefaultRegistrations` (open-for-extension, closed-for-modification).
- **SqlDestinationSchemaService** — introspects live `information_schema.columns` for SQL Server/PostgreSQL/MySQL so the mapping UI can offer real column pickers.

### Governance (`Governance/`)
- **CompositeGovernancePolicyService** — runs the ordered `IGovernanceRule` set; `ResourceTypeAccessGovernanceRule` (per-resource-type RBAC) and `ConsentGovernanceRule` are the shipped rules.
- **ConfiguredResourceTypeAccessPolicy** / **ConfiguredConsentService** / **FhirConsentParser** — resource-type allow/deny policy and FHIR Consent resource interpretation.
- **SafeHarborDeIdentificationService** — HIPAA Safe Harbor JSON redaction (remove/hash identifiers, dates→year, ZIP→3 digits) driven by configurable FHIR-path rules.
- **KAnonymityDeIdentificationService** — Expert-Determination set-level de-identification: generalizes quasi-identifiers and suppresses equivalence classes smaller than `k`.
- **ConfiguredRetentionPolicyService** + **RetentionPurgeService** — configurable retention windows and a purge over `IPurgeableStore`s.

### Messaging (`Messaging/`)
- **MessagingServiceCollectionExtensions** — selects the transport from `Messaging:Provider` (`InMemory` default, `RabbitMq`, `AzureServiceBus`); registers transport-agnostic command handlers and retry options regardless of provider.
- **AzureServiceBus/** and **RabbitMq/** — options, connection/topology, publisher, dispatchers (pipeline-run + webhook-ingestion), and generic consumers.
- **InMemory…** — channel, consumer, dispatchers, processed-message store for single-process dev.
- **PipelineRunCommandHandler** / **WebhookIngestionCommandHandler** — the actual work executed off the queue.
- **Ef/InMemoryProcessedMessageStore** + **ProcessedMessage** — idempotency (durable, multi-instance vs. per-process).
- **MessageRetry** / **MessageProcessingOptions** — exponential-backoff retry policy.

### Normalization (`Normalization/`)
- **CompositeResourceNormalizationService** — runs ordered `IResourceNormalizationStep`s, isolating failures so one bad stage never fails the resource.
- **Steps/** — `ExtensionFlatteningNormalizationStep`, `UsCoreValidationNormalizationStep`, `DataQualityScoringNormalizationStep`, `PatientMatchingNormalizationStep`.
- **FhirPatientMatchService** — FHIR `Patient/$match` (MPI) with deterministic fallback.
- **TerminologyMappedRecordNormalizationService** — applies terminology translation to mapped records.

### Terminology (`Terminology/`)
- Four capabilities — **Lookup**, **Translation**, **Validation**, **Expansion** — each with a `Local`, `Fhir`, and `Composite` implementation (local first, remote FHIR fallback). Lookup and Translation add `Caching…` decorators backed by `IDistributedCache`.
- **UsCoreValueSetCatalog** — the local US Core required-binding value-set catalog used by validation.

### Security (`Security/`)
- **CompositeSecretProvider** over **AzureKeyVaultSecretProvider** + **ConfigurationSecretProvider** — Key Vault with configuration fallback.
- **Pbkdf2PasswordHasher** — PBKDF2 password hashing.
- **LocalIdentitySeedService** — seeds default users/roles/permissions.
- **SystemCurrentUserService** — fallback actor for hosts with no HTTP context (Worker, migrations).

### Sources (`Sources/`)
- **SourceCapabilityDiscoveryService** — reads a source's `/metadata` CapabilityStatement and persists a `SourceCapabilityProfile`.
- **SourceConnectionTestService** — verifies token acquisition and reachability.

### Pipeline (`Pipeline/`)
- **ConfiguredPipelineService** — orchestrates the configured pull pipeline (source → map → normalize → govern/de-identify → write → audit/lineage).
- **FhirSubscriptionManagementService** — manages FHIR Subscription resources for push ingestion.
- **ScheduleExpressionMatcher** — schedule-expression matching helper.

### Scheduling (`Scheduling/`)
- **ScheduleEvaluationService** — multi-tenant due-route evaluation with catch-up, claiming and stamping each due route.
- **ScheduleDispatcher** — dispatches claimed runs onto the messaging transport.

### Health (`Health/`)
- **SqlServerConnectionHealthCheck** and **KeyVaultConfigurationHealthCheck** — registered with `AddHealthChecks`.

### Hl7v2 (`Hl7v2/`)
- **Hl7MessageProcessor** — parses an inbound HL7 v2 message, maps it to a FHIR Bundle, enqueues a `WebhookIngestionCommand`, and returns the MLLP AA/AE ACK; transport-agnostic so the socket listener (a Worker hosted service) stays thin. **Hl7MllpOptions** binds listener settings.

### Aggregation (`Aggregation/`)
- **PatientAggregationService** — best-effort, pass-through, patient-scoped read. Resolves the tenant's source, fans out one query per requested resource type (plus the Patient root) with bounded parallelism via the runtime connector layer, and records per-type failures as `OperationOutcome`s without touching the write-side pipeline.

## Dependencies
- **Projects:** `FHIRBridge.Application`, `FHIRBridge.Domain`, `FHIRBridge.Runtime.Application`, `FHIRBridge.Runtime.Domain`, `FHIRBridge.Runtime.Infrastructure`, `FHIRBridge.Integration` (BuildingBlocks).
- **Key packages:**
  - `Microsoft.EntityFrameworkCore` (+ `.SqlServer`, `.Tools`) and `Microsoft.Data.SqlClient` — persistence, migrations, design-time tooling.
  - `Npgsql`, `MySqlConnector` — PostgreSQL/MySQL destination + schema introspection.
  - `Azure.Identity`, `Azure.Security.KeyVault.Secrets` — Key Vault secrets via `DefaultAzureCredential`.
  - `Azure.Messaging.ServiceBus`, `RabbitMQ.Client` — async messaging transports.
  - `Microsoft.Extensions.Caching.StackExchangeRedis` / `.Memory` — distributed cache (Redis in prod, in-memory fallback) for token + terminology caches.
  - `Microsoft.Extensions.Http.Resilience` — standard retry/circuit-breaker/timeout applied to all outbound `HttpClient`s.
  - `Parquet.Net` (Parquet writer), `QuestPDF` (PDF writer), `SSH.NET` (SFTP writer).
  - `Microsoft.Extensions.Diagnostics.HealthChecks`, `…Options`, `…Configuration.Abstractions`, `…DependencyInjection.Abstractions`, `…Logging.Abstractions` — health checks, options binding, DI/config/logging plumbing.
- **Referenced by:** the host/composition projects (Web API, Worker, and tests) — they call `AddFHIRBridgeInfrastructure(configuration)`. No inner layer references it.

## Current state in this skeleton
The skeleton contains only the project file and empty `bin`/`obj` — no source has been ported yet. The `.csproj` is already complete: it carries every NuGet package and all six project references the reference implementation uses, so the build wiring is in place; the implementations are what remain to be added.

What remains to be ported (everything described above): Persistence (DbContext, configurations, repositories, migrations, interceptor, design-time factory), the ~24 destination writers + factory + schema service, governance/de-identification, audit, messaging (all three transports), normalization, terminology, security, sources, pipeline, scheduling, health, HL7 v2, aggregation, and the `DependencyInjection.AddFHIRBridgeInfrastructure` composition root that ties them together.

Intentional skeleton caveat: a few types — notably **FHIRBridgeDbContext**, **AuditingSaveChangesInterceptor**, and **RelationalDestinationWriterBase** — were trimmed from the skeleton because they bind tightly to the concrete domain (entity sets, `IAuditableEntity`/`ISoftDeletable` conventions, mapping-profile field shapes). They must be ported after the corresponding Domain/Application types exist; porting them before then would not compile.

## Roadmap — what it will do in detail
- **Persistence-first bring-up.** Port `FHIRBridgeDbContext`, the entity configurations, and the EF repositories, then run `InitialCreate` + `AddSourceCapabilityProfiles` migrations. Wire the `AuditingSaveChangesInterceptor` and the append-only audit guard so provenance stamping, soft delete, optimistic concurrency, and HIPAA-grade immutable audit work from day one. Until a connection string is present, `AddFHIRBridgeInfrastructure` will keep registering the in-memory repository/audit/lineage/processed-message variants so the platform runs database-free for dev and tests.
- **Configured destinations.** Bring up `RelationalDestinationWriterBase` and the SQL-family writers first (SQL Server/Azure SQL, PostgreSQL, MySQL, Snowflake, Databricks), then the file/serialization writers (CSV/Excel, NDJSON, Parquet, Avro, Protobuf, PDF), cloud/transport writers (Blob, S3, SFTP), and API/BI/FHIR writers (REST, FHIR repository, Power BI, Tableau). All register through `ConfiguredDestinationWriterFactory.DefaultRegistrations`, so new destination types are additive. `SqlDestinationSchemaService` will feed real column pickers to the mapping UI.
- **Governance and compliance.** Stand up the composite governance pipeline (resource-type RBAC + FHIR consent), Safe Harbor field-level de-identification, optional k-anonymity Expert-Determination set-level de-identification (off by default, suppresses records when enabled), configurable retention with a purge service that never touches immutable audit, and durable lineage tracking.
- **Normalization + terminology.** Run the four-stage normalization pipeline (flatten → US Core validate → data-quality score → patient match) and the local-then-FHIR terminology services (lookup/translate/validate/expand) with distributed-cache decorators and the US Core value-set catalog backing required-binding validation. FHIR `Patient/$match` MPI activates when `PatientMatch:BaseUrl` is configured, with deterministic matching as the fallback.
- **Async + ingestion.** Make the messaging transport pluggable across in-memory, RabbitMQ, and Azure Service Bus via `Messaging:Provider`, with idempotency (durable `ProcessedMessages` table or per-process store), exponential-backoff retry, and the pipeline-run/webhook command handlers. HL7 v2 messages arrive over MLLP, get mapped to FHIR Bundles, and flow through the same webhook-ingestion command path.
- **Resilience, secrets, and ops.** Apply the standard HTTP resilience handler (retry + circuit breaker + attempt/total timeouts) to every outbound `HttpClient` (EHR sources, REST/FHIR/cloud destinations, terminology, source tests). Resolve secrets from Key Vault with configuration fallback, hash passwords with PBKDF2, seed local identity, and expose SQL Server + Key Vault health checks. Source capability discovery, connection testing, FHIR Subscription management, multi-tenant schedule evaluation/dispatch, and synchronous best-effort patient aggregation round out the operational surface — all assembled by the single `AddFHIRBridgeInfrastructure` composition root.
