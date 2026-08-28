# FHIRBridge.Runtime.Infrastructure

> The infrastructure layer of the pipeline-execution side: concrete EHR auth grants, FHIR source/bulk-export/subscription connectors, destination writers, the in-memory run store, and the workflow node executors that back the ranked-workflow engine.

**Layer:** Runtime Infrastructure · **SDK:** Microsoft.NET.Sdk · **Target:** net9.0

## Purpose
This project implements the ports defined in `FHIRBridge.Runtime.Application` against real technology: it talks OAuth/SMART to EHR token endpoints, calls FHIR R4 servers (paginated search, Bulk Data `$export`, Subscriptions), writes resources to SQL Server (or an in-memory buffer), caches tokens in a distributed cache, and provides the concrete node executors that the ranked workflow orchestrator runs. It is the only runtime layer that knows about HTTP, certificates, SQL, and the Firely FHIR SDK, and it wires everything up through `AddRuntimeInfrastructure(...)`.

## Responsibilities
- Acquire EHR access tokens across the grant types Segue supports and select the right one per source, caching tokens in a distributed cache with an expiry skew.
- Mint SMART Backend Services client-assertion JWTs (RS384) and PKCE verifiers/challenges for public clients.
- Call FHIR R4 source servers: paginated `_count` search with retry/backoff/throttling, Bulk Data `$export` (kick-off → poll → NDJSON download), and rest-hook `Subscription` lifecycle management.
- Configure outbound HTTP with optional mutual TLS and tuned connection pooling per source.
- Write transformed resources to a destination (SQL Server with auto-created schema/tables + PHI-free audit rows, or an in-memory buffer) selected via a registration-driven factory.
- Persist pipeline runs and events (in-memory store for dev/test).
- Provide the concrete `IWorkflowNodeExecutor` implementations for every catalog node type and register them for the ranked-workflow engine.

## Key components
### `Auth/`
- `CompositeFhirAccessTokenProvider.cs` — selects the grant per `RuntimeSourceType`: Healow (auth-code + PKCE), MEDITECH Greenfield (confidential JSON), SMART Backend Services JWT (Epic, when a private key is present), or OAuth2 client-credentials (Cerner/Allscripts/generic, when a client secret is present); unauthenticated sources get an empty token.
- `EpicAccessTokenProvider.cs` — SMART Backend Services client-credentials flow using an RS384 client assertion; caches the token, audits success/failure.
- `OAuth2ClientCredentialsTokenProvider.cs` — standard OAuth2 `client_credentials` grant.
- `HealowAuthorizationCodeTokenProvider.cs` — interactive authorization-code + PKCE flow; builds the authorization request, exchanges the code from the OAuth callback, stores the token, and refreshes it on read when near expiry.
- `MeditechGreenfieldTokenProvider.cs` — MEDITECH Greenfield confidential-client JSON token exchange.
- `BackendServicesJwtFactory.cs` — builds the signed RS384 JWT client assertion (`iss/sub/aud/jti/iat/nbf/exp`, optional `kid`) from a PEM private key.
- `Pkce.cs` — RFC 7636 S256 code-verifier/challenge helpers.
- `DistributedFhirAccessTokenCache.cs` / `InMemoryFhirAccessTokenCache.cs` — `IFhirAccessTokenCache` over `IDistributedCache` (Redis or in-process) with a 1-minute expiry skew, and a pure in-memory fallback.
- `InMemoryFhirAuthorizationCodeTokenStore.cs` — `IFhirAuthorizationCodeTokenStore` for the auth-code tokens.

### `Connectors/`
- `EpicFhirSourceClient.cs` — the workhorse `IFhirSourceClient`: bearer-authenticated paginated FHIR search (`_count`, follows `next` links up to `MaxPages`), transient-error retry with exponential backoff + jitter honoring `Retry-After`, and per-source request throttling. Reused for Cerner/Allscripts/Healow/MEDITECH/generic FHIR.
- `FhirRestBulkExportClient.cs` — Bulk Data `$export` "ping-pong": kick off with `Prefer: respond-async`, poll the `Content-Location` status URL honoring `Retry-After`, then stream/parse each NDJSON output file.
- `FhirRestSubscriptionClient.cs` — create/update/delete rest-hook `Subscription` resources on the source server.
- `FhirSourceClientFactory.cs` — resolves an `IFhirSourceClient` for a `RuntimeSourceType` from DI registrations (`DefaultRegistrations`); closed for modification — new sources are added by registration.
- `MutualTlsCertificateProvider.cs` — `MutualTlsOptions` + `FhirConnectionPoolOptions`; builds a pooled `SocketsHttpHandler`, attaching the mTLS client certificate (from base64 PFX or file) when enabled.
- `EpicFhirClientOptions.cs`, `FhirBulkExportOptions.cs` — bound options for retry/timeout/throttle and bulk-export polling.
- `SampleFhirSourceClient.cs` / `SampleResources.cs` — offline sample source returning canned resources for demos/tests.

### `Destinations/`
- `SqlServerDestinationWriter.cs` — opens a SQL connection, ensures the schema + `FhirResources` and `PipelineResourceAudit` tables exist, inserts each resource (with a SHA-256 content hash) and a PHI-free audit row.
- `InMemoryDestinationWriter.cs` / `InMemoryDestinationBuffer.cs` — buffers writes in memory for dev/test.
- `DestinationWriterFactory.cs` — resolves an `IDestinationWriter` for a `RuntimeDestinationType` from `DefaultRegistrations` (SqlServer, InMemory).

### `Persistence/`
- `InMemoryPipelineRunStore.cs` — thread-safe `ConcurrentDictionary`-backed `IPipelineRunStore` for runs and events.

### `Workflows/`
- `WorkflowInfrastructureServiceCollectionExtensions.cs` — `AddWorkflowInfrastructure()` registers an `IWorkflowNodeExecutor` for every catalog node type (sources, transforms, terminology, compliance, 20+ destinations, audit-lineage, analytics).
- `Executors/SourceNodeExecutors.cs` — `SourceNodeExecutor` base + per-EHR subclasses; reads a `FhirSourceConfiguration` from node config and calls the source client factory, emitting a `ResourceBatch`.
- `Executors/TransformNodeExecutors.cs` — normalization, data-quality, flatten-extensions, patient-matching, mapping, repeating-array mapping, and terminology executors (several wired to real `FHIRBridge.Application` services).
- `Executors/DestinationNodeExecutors.cs`, `ComplianceNodeExecutors.cs`, `AnalyticsNodeExecutors.cs` — the destination, compliance (consent, US Core validation, de-identification, audit-lineage), and analytics (HEDIS, anomaly detection, patient aggregation) executors.

### Composition
- `DependencyInjection.cs` — `AddRuntimeInfrastructure(configuration)` wires the run store, token providers (each `HttpClient` configured for mTLS), the composite token provider, source/destination factories + their registrations, bulk-export and subscription clients, the distributed token cache, and binds options from configuration (`Runtime:Epic`, `Runtime:BulkExport`, `Connectivity:Mtls`, `Connectivity:ConnectionPool`).

## Dependencies
- **Projects:** `FHIRBridge.Runtime.Application`, `FHIRBridge.Runtime.Domain`, `FHIRBridge.Application`, `FHIRBridge.Domain`, `FHIRBridge.Integration` (BuildingBlocks — provides `FhirResourceParser`, `SqlServerConnectionFactory`).
- **Key packages:** Hl7.Fhir.R4 6.2.0, Hl7.Fhir.Serialization 4.3.0, Microsoft.Data.SqlClient 5.2.2, Azure.Identity 1.21.0, Azure.Security.KeyVault.Secrets 4.11.0, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Caching.{Abstractions,Memory}`, `Microsoft.Extensions.Configuration.Abstractions`, EF Core Tools 9.0.10 (build-time).
- **Referenced by:** the runtime hosts (Runtime.Api / Worker) that compose `AddRuntimeApplication()` + `AddRuntimeInfrastructure()`.

## Current state in this skeleton
Empty — the `.csproj` exists (with all the project + package references above) but contains no `.cs` files. Nothing under `Auth/`, `Connectors/`, `Destinations/`, `Persistence/`, or `Workflows/` has been ported, and `AddRuntimeInfrastructure(...)` does not yet exist.

**Note:** in the reference implementation the workflow executors share an abstract `WorkflowNodeExecutorBase` (a placeholder base that returns a stub payload and exposes config-reading helpers, with select executors overriding `ExecuteAsync` to call real clients/services). That base class was **trimmed from this skeleton** — when porting the `Workflows/Executors/`, reintroduce `WorkflowNodeExecutorBase` (or fold its helpers into each executor) before the source/transform/destination executors will compile.

## Roadmap — what it will do in detail
- **One client, many EHRs.** Because Epic, Cerner, Allscripts, Healow, MEDITECH Greenfield, and generic R4 servers all speak the same REST surface, they reuse `EpicFhirSourceClient`; only the *token grant* differs, and `CompositeFhirAccessTokenProvider` picks it per source. Adding a new EHR is typically just a new `FhirSourceClientRegistration` plus (if its auth is novel) a new token provider — the factory and orchestrator stay closed for modification.
- **Resilient, well-behaved source calls.** The source client paginates with bounded page counts, retries only transient failures (408/429/5xx) with exponential backoff + jitter, respects `Retry-After`, and throttles per source connection so Segue stays within EHR rate limits. Bulk export follows the asynchronous Bulk Data spec end-to-end, and subscriptions let sources push changes back.
- **Secure transport and secrets.** Outbound calls can present a mutual-TLS client certificate (sourced inline as base64 PFX or from disk, and in production from Key Vault via Azure.Identity), over a tuned, pooled `SocketsHttpHandler`. Tokens live in a distributed cache (Redis in prod) shared across API and Worker, with an expiry skew so a cached token is always still usable.
- **Destinations as a growth axis.** SQL Server is the reference sink today — it self-provisions its schema/tables and writes a PHI-free audit row alongside each resource. The factory + registration pattern (mirrored by the 20+ destination node types in the workflow catalog) is the path to Azure SQL, Postgres, Snowflake, blob/NDJSON/Parquet, FHIR repositories, and analytics targets; each becomes a new `IDestinationWriter` / executor without changing the orchestrator.
- **Workflow executors back the designer.** Each visual-designer node maps to an executor here. Source executors already call the real connectors; transform/compliance/analytics executors are progressively being wired from placeholder behavior to the real `FHIRBridge.Application` services (mapping, normalization, terminology, de-identification, US Core validation), so a graph drawn in the portal runs against production logic with full per-node lineage and audit. Persistence of runs/events and workflow definitions will move from the in-memory stores to EF/SQL for durability.
