# 04 — App Services & Building Blocks

> Part of the [Backend Architecture Guide](README.md). Reviewed 2026-07-01 (`port/stepbase-overlay`).

## Application services (`AddFHIRBridgeApplication`, `src/FHIRBridge.Application/Services`)

**Auth / RBAC**
- `LocalAuthService` — local login, JWT + refresh, failed-login lockout, forgot/reset password.
- `UserManagementService` — user CRUD, invite / accept (48h token), activate/deactivate, role assign/remove.
- `RoleManagementService` — role CRUD, permission assign; blocks deletion of system roles.
- `TenantRegistrationService` — self-serve onboarding: seeds 4 tenant roles (SuperAdmin/WorkflowDesigner/Operator/Viewer) + admin user + immediate JWT.
- `UserAccessService` — current-user profile, tenant members, assign user↔tenant.

**Configuration**
- `UnifiedTenantConfigurationService` — all tenant/source/webhook/destination/mapping/route CRUD; validates a mapping's resource type against the source CapabilityStatement; cascade-disables dependents.
- `YamlManifestImportService` — bulk config import from a YAML manifest.

**Mapping**
- `JsonMappingEngine` — JSONPath → relational, array policies (Scalar / RepeatParent / SeparateDestination / StoreJson), type conversion.
- `DefaultMappingMaterializer` — orchestrates extract → map → normalize → de-id → write.
- `EmbeddedFhirElementCatalog` — FHIR R4 element catalog powering mapping-UI autocomplete.

**Reporting / analytics**
- `HedisMeasureReportService` (FHIR MeasureReport), `RunAnomalyDetectionService` (heuristic run anomalies), `PipelineRunMetricsService`.

> Governance/normalization/de-id/retention services registered here in the *core Application* layer are **pass-through placeholders**; the real implementations are in Infrastructure (below).

## Infrastructure capabilities (`AddFHIRBridgeInfrastructure`)

Beyond persistence (see [01](01-domain-and-data.md)):
- **21 destination writers** — SQL Server, PostgreSQL, MySQL, Snowflake, Databricks, Power BI, Tableau, S3, Azure Blob, SFTP, REST API, FHIR repository, CSV, Excel, NDJSON, Parquet, Avro, Protobuf, PDF, in-memory (via `ConfiguredDestinationWriterFactory`).
- **Terminology** — Local + FHIR-remote composite lookup/translate/validate/expand, distributed-cache wrapped.
- **Normalization pipeline (4 steps)** — extension flattening → US-Core validation → data-quality scoring → patient matching (`CompositeResourceNormalizationService`).
- **Governance rule engine** — composable RBAC + consent + de-identification (`SafeHarborDeIdentificationService`, optional k-anonymity) + retention/purge.
- **Secrets** — config + Azure Key Vault composite provider.
- **Resilience** — HTTP retry + circuit breaker (30s attempt / 120s total).
- **Cache** — Redis when configured, else memory.
- **Messaging** — InMemory / RabbitMQ / Azure Service Bus (by `Messaging:Provider`).
- **Health checks** (SQL, Key Vault). `AddRuntimeInfrastructure` wires the workflow engine + HL7 MLLP.

## Building blocks (`src/BuildingBlocks`)

**Built & in use**
- `SharedKernel` — base abstractions (`Entity`, `AggregateRoot`, `AuditableEntity`, `ISoftDeletable`), `Result`/`Error` pattern, domain exceptions, metrics contracts (`IPipelineMetrics`, `IMetricsSnapshotProvider`); zero external deps.
- `Integration` — FHIR JSON parse/build (partial-failure bundles), HL7 v2 parser, MLLP framing/ACK, HL7→FHIR mapper (ADT/ORU/MDM), SQL Server connection/identifier helpers.
- `Observability` — OpenTelemetry tracing + metrics, Serilog (console/Seq/App Insights), **PHI-masking log enricher**, one-call DI.

**Stubs (package refs, no code yet)**
- `Contracts` (shared message DTOs), `EventBus` (MassTransit + Azure Service Bus pub/sub), `Messaging` (direct Service Bus client). The Worker already references EventBus.

## Control plane (`src/ControlPlane`)
`ControlPlane.Domain` + `ControlPlane.Application` are **empty scaffolds** (`.csproj` only, not in the solution). Intended future split: Control Plane = configuration authoring / validation / publish lifecycle; Runtime = execution. Today both live in the core Domain/Application.

## Tests (`tests/`, ~104 methods)
- `FHIRBridge.ArchitectureTests` (~5) — clean-layering rules + no-`switch`-on-`ApplicationType` (registry/strategy enforcement).
- `FHIRBridge.Api.IntegrationTests` (~61, `WebApplicationFactory` + in-memory DB) — auth, users, roles, permissions, tenant registration.
- `FHIRBridge.Runtime.UnitTests` (~26) — ApplicationType strategy registry, OAuth/SMART flows, ranked workflow orchestration + graph validation.
- `FHIRBridge.UnitTests` (~12) — source → application-type mapping, interactive source authorization.
- `FHIRBridge.LoadTests` — NBomber placeholder (`IsTestProject=false`, run manually).
