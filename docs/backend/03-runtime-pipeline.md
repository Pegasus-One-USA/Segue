# 03 — Runtime Pipeline

> Part of the [Backend Architecture Guide](README.md). Reviewed 2026-07-01 (`port/stepbase-overlay`).
> Projects: `src/Runtime/*` + `src/Worker`. This is the pipeline execution engine.

## Pipeline model

- **`PipelineRun`** (`Runtime.Domain`) — tracks a run: status, extracted/written counts, `PipelineRunStep` (per resource type per stage), `PipelineRunEvent` (audit).
- **`PipelineGraph`** — explicit DAG; default `Extraction → Governance → Transform → Output`. Validates cycles/dangling deps and topologically sorts.
- **`PipelineOrchestrator`** — **bounded parallel fan-out** (extract resource types concurrently, max 4) → **single-thread fan-in** (govern → transform → output sequentially so run-state mutation is deterministic).
- **`ResourceEnvelope`** — immutable value object flowing through stages: `(ResourceType, ResourceId, RawJson, VersionId, LastUpdated)`.

## Sources / connectors

- **`RuntimeSourceType`**: Epic, Healow, MeditechGreenfield, GenericFhir, Sample (Cerner/Allscripts gated, reuse Epic path).
- Hierarchy: `ISourceConnector` (FhirRest / Hl7v2 / FlatFile / Database) → `IFhirSourceClient` → `FhirSourceConnectorBase` (paginated `_count` search, transient retry + jitter + `Retry-After`, per-source throttle).
- `FhirSourceClientFactory` — registry lookup; **Epic + Sample enabled** in Phase 1.
- Config: `FhirSourceConfiguration` (BaseUrl, ClientId, TokenEndpoint, PrivateKeyPem, ClientSecret, AuthorizationEndpoint, Scopes, pagination, optional `ApplicationType`).

## Authentication — two axes

**Vendor axis = inheritance** (subclass `FhirSourceConnectorBase`). **Application-type axis = composition** via a strategy registry. No `switch` (enforced by architecture tests).

**Token providers** (`Runtime.Infrastructure/Auth`):

| Provider | Grant / use |
|---|---|
| `EpicAccessTokenProvider` | SMART Backend Services (client_credentials + RS384 `private_key_jwt`) |
| `OAuth2ClientCredentialsTokenProvider` | OAuth2 client_credentials (confidential clients) |
| `SmartAuthorizationCodeTokenProvider` | Authorization code + PKCE (interactive, vendor-neutral) |
| `HealowAuthorizationCodeTokenProvider` | Auth code + PKCE (Healow, vendor-pinned) |
| `EpicInteractiveTokenProvider` | Auth code + PKCE (Epic interactive) |
| `MeditechGreenfieldTokenProvider` | Confidential JSON exchange |

`CompositeFhirAccessTokenProvider` selects at runtime: if `ApplicationType` set → strategy; else legacy inference (Healow → PKCE, MEDITECH → confidential, private key → backend JWT, secret → client_credentials). `BackendServicesJwtFactory` signs RS256/RS384. Token cache = `DistributedFhirAccessTokenCache` (Redis) / InMemory. **Interactive tokens + in-flight OAuth state are stored in the distributed cache** (`DistributedFhirAuthorizationCodeTokenStore` + `DistributedOAuthAuthorizationStateStore` over `IDistributedCache`; Redis in prod, memory in dev) so the `authorize`/`launch` → `callback` round-trip can land on any node and a later pipeline run (API or Worker) reads the token back — the interactive app types survive restart and multi-instance, not just a single synchronous run.

## ApplicationType strategies

`ISourceApplicationStrategy` (registry `SourceApplicationStrategyRegistry`, O(1) resolve). Each exposes `Describe()` / `Validate(source)` / `GetAccessTokenAsync(source)`:

| Strategy | Scopes | Interactive | PKCE |
|---|---|---|---|
| `BackendServicesApplicationStrategy` | `system/*` | No | No |
| `EhrLaunchApplicationStrategy` | `user/*` + launch | Yes | Yes |
| `StandaloneApplicationStrategy` | `user/*` | Yes | Yes |
| `PatientApplicationStrategy` | `patient/*` | Yes | Yes |

## Destinations (Runtime plane)

`RuntimeDestinationType`: **SqlServer** (auto-creates schema + `FhirResources` / `FhirAudit` tables, SHA-256 dedup) and **InMemory**. `DestinationWriterFactory` registry.
> The *core* `FHIRBridge.Infrastructure` has a separate, larger set of **21** destination writers — see [04 — App Services & Building Blocks](04-app-services-and-building-blocks.md).

## Transform
`IResourceTransformer`; Phase-1 impl `FhirResourceNormalizer` only compacts JSON. Field mapping + de-identification here are future work (the core Application/Infrastructure path already has richer mapping/normalization).

## Ranked workflow engine
`IRankedWorkflowOrchestrator` runs a `WorkflowDefinition` (DAG of ranked `WorkflowNode`s; categories Source / Transform / Compliance / Analytics / Destination). Topo-sorts by `(Rank, SubRank)`, resolves `IWorkflowNodeExecutor` from a registry, threads node outputs, records `WorkflowAuditEvent`. Node types + executors are pre-built; definition store + audit recorder are in-memory in Phase 1. Exposed via the API workflow endpoints.

## Worker host (`src/Worker`)

| Service | Role | Trigger |
|---|---|---|
| `Worker` | Find due scheduled routes, call `IConfiguredPipelineService.StartAsync` | Timer (default 30s) |
| `PipelineRunCommandProcessor` | Consume `PipelineRunCommand` messages and execute | Message transport |
| `ScheduleDispatcherWorker` | Enqueue due scheduled runs (Phase 2+) | Timer |
| `Hl7MllpListenerService` | Listen for HL7 v2 over MLLP (TCP), route to webhook | TCP listener |

Each is gated by options (`RuntimeWorkerOptions`, `ScheduleDispatcherOptions`, `Hl7MllpOptions`); most are disabled by default.
