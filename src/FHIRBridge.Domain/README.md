# FHIRBridge.Domain

> The configuration/control-plane domain model — source connections, mappings, routes, RBAC, and the FHIR vocabulary the platform understands. Pure business rules, no infrastructure. Single-org: there is no tenant boundary.

**Layer:** Domain · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
`FHIRBridge.Domain` is the innermost layer of the Clean-Architecture solution. It holds the configuration-side domain model that describes *what* the organization has wired up (sources, destinations, mappings, pipeline routes, webhooks) and *who* may touch it (users, roles, permissions). The platform is single-org: configuration entities are flat and top-level rather than nested under a tenant aggregate. All entities are persistence-ignorant POCOs with encapsulated state (private setters, factory/`Update` methods) and enforce their own invariants. It depends on nothing but the shared kernel, and every other layer depends on it.

## Responsibilities
- Own the **flat configuration entities** — source connections, webhook configurations, destination configurations, mapping profiles, and resource pipeline routes — as top-level entities for the single organization, each enforcing its own invariants (enable/disable cascades, duplicate-route prevention).
- Encode the **single-source-of-truth model** where a `ResourcePipelineRoute` derives its FHIR resource type, source connection, and destination entirely from its `MappingProfile` — there is no separate `ResourceConfiguration` entity.
- Define the **FHIR vocabulary** the platform supports: which resource types can be mapped/extracted, which resource types are aggregatable in a patient-compartment read, and the rules for normalizing and resolving those types.
- Own the **RBAC model** (users, roles, permissions, and their join entities) including local-login/security hardening state (lockout, password reset, MFA flag) and role assignment.
- Own **audit and lineage records**: an immutable operational audit log, a tamper-evident (hash-chained) user-activity audit log, and a purgeable resource-lineage trail — all PHI-free.
- Hold **value objects** for cross-cutting concerns: mapping field definitions (including N-level array policy), source authentication, secret references, and discovered source capabilities.
- Stay free of EF Core, HTTP, JSON, and DI — those live in Infrastructure/Application. Domain references only `FHIRBridge.SharedKernel`.

## Key components

### Entities/
Configuration is single-org and flat: `SourceConnection`, `WebhookConfiguration`, `DestinationConfiguration`, `MappingProfile`, and `ResourcePipelineRoute` are independent top-level entities (there is no `Tenant` aggregate root wrapping them). A `ResourcePipelineRoute` derives its type/source/destination from its bound `MappingProfile`.

- **`MappingProfile`** (`AuditableChildEntity<Guid>`) — the single source of truth for a route's `ResourceType`, `SourceConnectionId`, `DestinationId`, plus a `DestinationObject` (target table/object) and an ordered list of `MappingField` value objects.
- **`SourceConnection`** — a configured upstream FHIR/HL7 endpoint: `SourceSystemType`, `BaseUrl`, and a `SourceAuthenticationConfiguration`.
- **`DestinationConfiguration`** — a configured downstream sink: `DestinationType`, a `SecretReference`, and an optional `Target`.
- **`ResourcePipelineRoute`** — binds a webhook (optional) + mapping into a runnable pipeline; carries `IngestionMode`, cron `ScheduleExpression`, `SearchParameters`, `Priority`, and `LastTriggeredOnUtc` for catch-up scheduling.
- **`SourceCapabilityProfile`** — a point-in-time snapshot of a source's CapabilityStatement (`/metadata`): supported `CapabilityResource`s, configured scopes, raw JSON, discovery timestamp. A standalone entity so discovery refreshes independently. `SupportsResourceType` gates whether a mapping may bind to a type.
- **`WebhookConfiguration`** — a per-source, per-resource-type inbound webhook endpoint (`Path`, `ResourceType`, enable flag).
- **`ConfiguredPipelineRunRecord`** (`Entity<Guid>`) — a completed run's outcome: status, resource-type list, extracted/mapped/written counts, errors, timing, and `TriggeredBy`/`TriggerType` (Manual/Scheduled/Webhook/Bulk).
- **`OperationalAuditLog`** (`Entity<Guid>`) — immutable PHI-free system/pipeline event with full FK context (run, route, source, destination, mapping), action/status/message, resource count, correlation id.
- **`ResourceLineageEntry`** (`Entity<Guid>`) — one step in a resource's chain of custody (access → normalize → de-id → output); PHI-free, purgeable under retention.
- **`UserActivityAuditLog`** (`Entity<Guid>`) — append-only end-user action record sealed into a SHA-256 hash chain (`PreviousHash` + `EntryHash` via `SealChain`) for HIPAA §164.312(b)/(c) tamper evidence.
- **RBAC:** **`User`** (external id, local-login hash, lockout/MFA/password-reset state), **`Role`** (system vs. custom), **`Permission`** (categorized, system flag), and join entities **`RolePermission`**, **`UserRole`** (role assignment). Each join carries an `IsEnabled` soft-disable flag.

### ValueObjects/
- **`MappingField`** (record) — one field mapping: `TargetField`, `JsonPath`, `MappingValueType`, required flag, default, format, optional terminology system/code paths, normalization type, and `ArrayPolicy`/`Cardinality`/`ArrayAncestors` for N-level array handling.
- **`SourceAuthenticationConfiguration`** — auth type plus client id, token endpoint, scopes, and `SecretReference`s for client secret / private key (SMART backend services, OAuth client-credentials, API key).
- **`SecretReference`** (record) — a `(KeyVaultName, SecretName)` pointer; secrets are never stored in the domain.
- **`CapabilityResource`** (record) — one resource type a source exposes plus its supported interaction codes.

### Enums/
- **`SourceSystemType`** (Epic, Cerner, Athenahealth, Allscripts, Healow, Meditech, generic FHIR, HL7v2, Sample), **`DestinationType`** (~21 sinks: SQL Server/Azure SQL/Postgres/MySQL, REST API, FHIR repo, Blob/S3/SFTP, CSV/Excel/NDJSON/Parquet/Avro/Protobuf/PDF, Power BI/Tableau, Snowflake/Databricks, in-memory), **`AuthenticationType`**, **`IngestionMode`** (Webhook/ScheduledPull/both), **`MappingValueType`**, **`ArrayPolicy`** (Scalar, FirstItem, RepeatParent, SeparateDestination, StoreJson, RejectIfMultiple), **`UserStatus`**, **`PatientSelectionMethod`**.

### Fhir/
- **`SupportedFhirResourceTypes`** — the write-pipeline allow-list (Patient, Observation, Condition, MedicationRequest, AllergyIntolerance, Encounter, DiagnosticReport, Procedure, Immunization) with `IsSupported`/`Normalize`.
- **`PatientCompartmentResourceTypes`** — the read-aggregation allow-list (adds DocumentReference), kept deliberately separate from the write list.
- **`PatientCompartmentResolver`** — resolves an `include=all|CSV` parameter into the deterministic set of compartment types to query (Patient excluded).
- **`UnsupportedResourceTypeException`** — typed failure for an unknown aggregation type.

## Dependencies
- **Projects:** `FHIRBridge.SharedKernel` (base types `Entity<T>`, `AuditableEntity<T>`, `AuditableChildEntity<T>`, `IAuditableEntity`, `ISoftDeletable`, `Result`/`Error`, exceptions).
- **Key packages:** none (intentionally — keeps the domain pure).
- **Referenced by:** `FHIRBridge.Application`, `FHIRBridge.Infrastructure`, and (transitively) the API, Worker, and Gateway hosts.

## Current state in this skeleton
The skeleton contains only the `FHIRBridge.Domain.csproj` (referencing `FHIRBridge.SharedKernel`) and compiles to an empty assembly. **None** of the entities, value objects, enums, or the `Fhir/` helpers listed above have been ported yet. To reach parity with the reference implementation, port (in dependency order): `Enums/` and `ValueObjects/` first, then `Fhir/`, then the flat configuration `Entities/`, and finally the RBAC and audit entities.

## Roadmap — what it will do in detail
The Domain layer will remain the stable contract at the center of the platform. As the product matures it will:
- Grow the **configuration entities** with richer governance invariants (data-residency enforcement, retention-window validation, schedule/time-zone rules) while keeping all mutation behind intention-revealing methods.
- Expand the **FHIR vocabulary** (`SupportedFhirResourceTypes` / `PatientCompartmentResourceTypes`) toward fuller FHIR R4 coverage, aligned with the generated `fhir-r4-catalog.json` consumed by the Application layer, and tighten capability-gating so a mapping can only bind to a resource type its source actually exposes.
- Deepen the **mapping value objects** to support full N-level array materialization (`ArrayPolicy`, `Cardinality`, `ArrayAncestors`) and terminology-aware field binding (system/code JSON paths feeding the terminology services).
- Harden the **RBAC and audit model** for compliance — the hash-chained `UserActivityAuditLog`, immutable `OperationalAuditLog`, and purgeable `ResourceLineageEntry` together give HIPAA-grade who/what/when traceability with PHI strictly excluded from the domain.
- Stay infrastructure-free so it can be unit-tested in isolation and reused unchanged across the API, scheduling Worker, and gateway hosts.
