# FHIRBridge.Application

> The control-plane application layer — use cases, service contracts (ports), DTOs, the JSON mapping engine, RBAC/security policy model, and YAML manifest import. Orchestrates the domain; owns no infrastructure.

**Layer:** Application · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
`FHIRBridge.Application` sits just outside the domain and implements the configuration/control-plane use cases of the platform. It defines the **ports** (abstractions) that Infrastructure implements — repositories, secret providers, destination writers, source/capability discovery, terminology, messaging, scheduling — and the **DTOs** that cross the API boundary. It also contains pure, infrastructure-free **services** that can run anywhere: the JSON mapping engine, governance/de-identification/retention defaults, the unified tenant-configuration orchestrator, user/role management, HEDIS measure reporting, run-anomaly detection, and YAML manifest import. It depends on `FHIRBridge.Domain` and `FHIRBridge.SharedKernel` plus the Runtime application layer.

## Responsibilities
- Define **service contracts (ports)** under `Abstractions/` for every external capability the control plane needs, grouped by concern (Aggregation, Audit, Destinations, Governance, Mapping, Messaging, Normalization, Persistence, Pipeline, Scheduling, Security, Sources, Terminology). Infrastructure supplies the implementations; the Application layer never touches EF Core or HTTP directly.
- Own the **configuration orchestration** (`UnifiedTenantConfigurationService`): create tenants, add/update/enable source connections, webhooks, destinations, mapping profiles, and routes — driving the `Tenant` aggregate, triggering source-capability discovery, and writing operational audit entries.
- Provide the **JSON mapping engine** (`JsonMappingEngine` + `IMappingMaterializer`) that evaluates `MappingField` JSON paths against source JSON, applies array policies (RepeatParent, SeparateDestination, StoreJson, RejectIfMultiple, FirstItem/Scalar), type-converts values, and materializes a parent table + child tables for a destination writer to persist.
- Own the **unified security model** (`Security/`): the five built-in roles, the permission catalog, authorization policies, role↔permission seed, current-user claim reading, and Entra group→role mapping.
- Own **identity/RBAC use cases**: local authentication (lockout, password reset, forced change), user management, role management, and identity seeding.
- Provide **analytics/reporting services**: HEDIS `MeasureReport` generation from pipeline-run history and statistical run-anomaly detection.
- Provide **YAML manifest import** (`YamlManifestImportService` + `Manifests/TenantManifest`): declaratively provision a whole tenant (sources, destinations, mappings, routes) from a single YAML document.
- Define **messaging commands** (`PipelineRunCommand`, `WebhookIngestionCommand`) and the dispatcher/handler/consumer ports that the Worker and queue infrastructure plug into.
- Carry the **generated FHIR R4 element catalog** (`Mapping/Catalog/fhir-r4-catalog.json`, copied to output) that powers the mapping designer's element picker via `IFhirElementCatalog` / `EmbeddedFhirElementCatalog`.

## Key components

### Abstractions/ (ports — implemented in Infrastructure)
- **Aggregation/** — `IPatientAggregationService` + `PatientAggregationResult` + `SourceConnectionUnavailableException`: synchronous, pass-through patient-compartment FHIR read (no mapping/destination/run).
- **Persistence/** — `ITenantConfigurationRepository`, `IConfiguredPipelineRunRepository`, `ISourceCapabilityRepository`, `IUserAccessRepository`.
- **Mapping/** — `IJsonMappingEngine`, `IMappingMaterializer` (+ `MaterializedDataset`), `IFhirElementCatalog`.
- **Sources/** — `ISourceConnectionTestService`, `ISourceCapabilityDiscoveryService` (discovers/persists `/metadata` snapshots).
- **Destinations/** — `IConfiguredDestinationWriter`, `IConfiguredDestinationWriterFactory`, `IDestinationSchemaService`.
- **Pipeline/** — `IConfiguredPipelineService` (start manual/webhook runs, list recent), `IFhirSubscriptionManagementService`.
- **Messaging/** — `IPipelineRunDispatcher`, `IWebhookIngestionDispatcher`, `IMessageConsumer`, `IProcessedMessageStore` (idempotency), and command-handler ports.
- **Scheduling/** — `IScheduleEvaluationService`, `IScheduleDispatcher`.
- **Governance/** — `IGovernancePolicyService`, `IDeIdentificationService` / `IDataSetDeIdentificationService`, `IRetentionPolicyService`, `ILineageTracker` / `ILineageStore`, `IPurgeableStore`, `IConsentService`, `IGovernanceRule`, `IResourceTypeAccessPolicy`.
- **Normalization/** — `IResourceNormalizationService`, `IMappedRecordNormalizationService`, `IPatientMatchService`.
- **Terminology/** — `ITerminologyLookupService`, `ITerminologyTranslationService`, `ITerminologyValidationService`, `ITerminologyExpansionService` (FHIR `$lookup`/`$translate`/`$validate-code`/`$expand`).
- **Audit/** — `IOperationalAuditService`, `IUserActivityAuditService`.
- **Security/** — `ISecretProvider`, `ICurrentUserService`, `IPasswordHasher`, `IAccessTokenIssuer`.

### Services/ (infrastructure-free implementations)
- **`UnifiedTenantConfigurationService`** — the central configuration orchestrator over the `Tenant` aggregate (+ capability discovery + audit).
- **`JsonMappingEngine`** — JSONPath evaluation with `[*]`/`[n]` fan-out, array-policy handling, type conversion, parent/child-table output.
- **`DefaultMappingMaterializer`**, **`EmbeddedFhirElementCatalog`** — materialization + element-catalog backing for the mapping designer.
- **`LocalAuthService`**, **`UserManagementService`**, **`RoleManagementService`**, **`UserAccessService`** — RBAC/identity use cases (+ `IIdentitySeedService`).
- **`YamlManifestImportService`** — parse + validate a `TenantManifest` and provision a tenant end-to-end.
- **`HedisMeasureReportService`** — builds FHIR `MeasureReport`s (pipeline-success / resource-write measures) from run history.
- **`RunAnomalyDetectionService`** (+ `AnomalyDetectionOptions`) — failure/write-ratio/error-rate/latency/throughput-drop heuristics over recent runs.
- **`PassThrough*` services** — default no-op normalization/de-id, plus `DefaultGovernancePolicyService` / `DefaultRetentionPolicyService`.
- **`PipelineRunMetricsService`**, and option types (`IncrementalSyncOptions`, `ExpertDeterminationOptions`, `PatientAggregationOptions`).

### Security/
- **`UnifiedRoles`** (GlobalAdmin, TenantAdmin, PipelineEngineer, Analyst, Auditor), **`UnifiedPermissions`** (tenants.read/write, configuration.write, pipeline.execute, auditlogs.read, sourceconnections.test), **`AuthorizationPolicies`**, **`UnifiedRolePermissionSeed`** + **`SeededSecurityIds`**, **`CurrentUserClaimReader`**, **`EntraAuthenticationOptions`** + **`EntraGroupRoleMapper`** (projects Entra group claims onto built-in roles).

### DTOs/ (~52)
Request/response contracts for the API: tenant/source/webhook/destination/mapping/route create+read DTOs, `MappingFieldDto`/`MappingTestResultDto`/`MappingChildTableDto`, `ConfiguredPipelineRunDto`, `OperationalAuditLogDto`, `SourceCapabilityProfileDto`, terminology result models, auth/identity DTOs (login, password change/reset, role/permission/user management), `HedisMeasureReportDto`, `RunAnomalyDto`, `ManifestImportResultDto`, `MappedDestinationRecord`, and more.

### Mappings/ & Manifests/ & Messaging/
- **`TenantConfigurationMapper`** — aggregate → DTO projection.
- **`TenantManifest`** — the YAML manifest shape.
- **`PipelineRunCommand`**, **`WebhookIngestionCommand`** — async messaging contracts.

## Dependencies
- **Projects:** `FHIRBridge.Domain`, `FHIRBridge.Runtime.Application`, `FHIRBridge.SharedKernel`.
- **Key packages:** `MediatR` (14.x), `FluentValidation` (+ DI extensions, 12.x), `Microsoft.Extensions.Logging.Abstractions` (10.x), `YamlDotNet` (18.x). Ships `Mapping/Catalog/fhir-r4-catalog.json` as a copy-to-output content asset.
- **Referenced by:** `FHIRBridge.Infrastructure`, `FHIRBridge.Api`, `FHIRBridge.Worker` (and other hosts that need the control-plane use cases).

## Current state in this skeleton
The skeleton contains the `FHIRBridge.Application.csproj` (with the package references and the `Mapping/Catalog/fhir-r4-catalog.json` content asset already present) and compiles to an essentially empty assembly. **None** of the `Abstractions/`, `DTOs/`, `Services/`, `Security/`, `Mappings/`, `Manifests/`, or `Messaging/` source files have been ported yet. Recommended porting order: `DTOs/` and `Abstractions/` first (they have the fewest dependencies), then `Security/`, then `Mappings/`/`Manifests/`/`Messaging/`, then the `Services/` implementations (mapping engine and `UnifiedTenantConfigurationService` last, as they depend on the most ports).

## Roadmap — what it will do in detail
The Application layer is the contract surface between the domain and everything operational. Going forward it will:
- Centralize all **control-plane use cases** behind the unified tenant-configuration service and MediatR handlers, keeping the API thin and the domain pure.
- Mature the **mapping engine** toward full FHIR R4 coverage and robust N-level array materialization, driven by the generated `fhir-r4-catalog.json` so the designer can offer accurate element pickers and the engine can faithfully shape parent/child destination datasets.
- Enforce **capability gating** at configuration time — a mapping may only bind to a resource type the source's discovered `SourceCapabilityProfile` actually supports — so failures surface at save time, not mid-run.
- Provide a complete **governance pipeline of ports** (de-identification, consent, retention/purge, lineage, terminology validation/translation) that Infrastructure fills in for HIPAA-grade processing, with safe pass-through defaults shipped here.
- Drive **asynchronous execution** via the messaging commands and dispatcher/handler/consumer ports, with idempotency (`IProcessedMessageStore`) and cron-based scheduling, so the Worker host can run pipelines reliably and at scale.
- Expand **analytics and compliance reporting** (HEDIS measures, anomaly detection, audit projections) and **identity** (Entra SSO group mapping, local auth hardening, role/permission administration) as the platform moves toward marketplace readiness.
