# FHIRBridge.Api

> The public-facing ASP.NET Core Web API host for FHIRBridge: versioned REST/JSON controllers, minimal-API workflow endpoints, JWT/Entra authentication, and the single-org administration and FHIR-aggregation surface that the Angular portal and external callers consume.

**Layer:** Host (Web API) · **SDK:** Microsoft.NET.Sdk.Web · **Target:** net9.0

## Purpose
`FHIRBridge.Api` is the HTTP entry point for the platform. It composes the Application, Infrastructure, Domain, and Runtime layers into a single Kestrel-hosted Web API, exposes the V1 controller surface and the workflow minimal-API endpoints, and enforces authentication and the unified-admin authorization policy. It is a thin host: business logic lives in the Application/Domain/Runtime layers and is invoked through injected services. The host's own responsibilities are request routing, security, serialization, CORS, Swagger, exception shaping, and DI composition.

## Responsibilities
- Bootstrap the web host (`Program.cs`): register controllers + JSON options (string-enum serialization), Swagger, `HttpContextAccessor`, and the Application/Infrastructure/Runtime DI extension methods (`AddFHIRBridgeApplication`, `AddFHIRBridgeInfrastructure`, `AddWorkflowCore`, `AddWorkflowInfrastructure`).
- Wire authentication and authorization: a policy scheme that forwards bearer tokens to either the local HS256 scheme or Microsoft Entra ID, plus the `UnifiedAdmin` authorization policy.
- Expose versioned (`api/v1/...`) REST controllers for auth, configuration, mapping, pipeline runs, observability, insights, lineage, audit, roles, users, subscriptions, source capabilities, webhook ingestion, and patient-scoped FHIR aggregation.
- Expose the workflow designer minimal-API endpoints (`/api/v1/workflows/...`) for CRUD, validate, run, activate/deactivate, and the node catalog.
- Apply the CORS `Portal` policy (configurable allowed origins, defaults to `localhost:4200`) so the SPA can call the API with credentials.
- Seed a local identity (super-admin) on startup in environments where `IIdentitySeedService` is registered.
- Shape unhandled exceptions into JSON problem responses via `GlobalExceptionHandlingMiddleware`.

## Key components

### Program.cs (composition root)
- Registers `ICurrentUserService` → `HttpContextCurrentUserService`, `IAccessTokenIssuer` → `JwtAccessTokenIssuer`, and the `IAuthorizationHandler` → `UnifiedAdminAuthorizationHandler`.
- Adds the `Portal` CORS policy, dev-only in-memory configuration defaults (local signing key + seed-admin credentials), Swagger (dev only), and runs `SeedLocalIdentity` before mapping controllers and workflow endpoints.

### Controllers (`Controllers/V1`)
- **AuthController** (`api/v1/auth`) — `GET me`, `POST login` (record login), and the local-auth flows `POST local/login`, `local/change-password`, `local/forgot-password`, `local/reset-password`. Local login/forgot/reset are `[AllowAnonymous]`; forgot-password reset tokens are suppressed unless `LocalAuth:ExposeResetTokens` is set.
- **FhirBridgeAggregationController** (`api/v1/fhirbridge`) — `GET Patient/{id}?include=all|CSV&source={id}`: synchronous, pass-through patient-scoped aggregation returning an `application/fhir+json` searchset Bundle. No mapping/destination/PipelineRun; best-effort fan-out across compartment resource types; emits a PHI-free `DataAccess` user-activity audit on every call and returns FHIR `OperationOutcome` errors.
- **ConfigurationsController** (`api/v1`) — the largest surface: single-org, flat configuration CRUD for source connections (create/update/deactivate/test), webhooks, destinations (+ schema), mapping profiles, resources, and resource pipeline routes. Successor to the deleted `TenantConfigurationsController`, with the `{tenantId}` route segment and tenant CRUD removed.
- **MappingController** (`api/v1/mapping`) — `POST test` (mapping test execution) plus the FHIR R4 catalog: `GET catalog/resources` and `GET catalog/resources/{resourceType}/fields`.
- **PipelineRunsController** (`api/v1/pipeline-runs`) — `POST` start a configured run (returns 201 synchronously, or 202 queued for bulk-export when a shared messaging transport is configured), `GET` recent runs, `POST {id}/deactivate`.
- **WebhookIngestionController** (`api/v1/webhooks/{webhookConfigurationId}/ingest`) — `[AllowAnonymous]` `POST` accepting a raw FHIR JSON payload; runs inline (202 with the run) or enqueues for the Worker when `WebhookIngestion:Async` is enabled.
- **ObservabilityController** (`api/v1/observability`) — `GET metrics`.
- **InsightsController** (`api/v1/insights`) — `GET measure-report`, `GET anomalies`.
- **LineageController** (`api/v1/lineage`) — `GET` data-lineage records.
- **OperationalAuditLogsController** (`api/v1/audit-logs`) — `GET` operational/user-activity audit log entries.
- **RolesController** (`api/v1/roles`) — list roles, list permissions, create/update/delete role.
- **UsersController** (`api/v1/users`) — list/create/update users.
- **SubscriptionsController** (`api/v1/subscriptions`) — create/delete FHIR subscriptions.
- **SourceCapabilitiesController** (`api/v1/source-connections/{sourceConnectionId}`) — `POST capabilities/discover`, `GET capabilities`, `GET catalog/resources` (capability-gated resource catalog).

All controllers except the anonymous local-auth and webhook-ingest endpoints require authentication; administrative controllers carry `[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]`.

### Security (`Security/`)
- **FhirBridgeAuthenticationExtensions** — `AddFhirBridgeAuthentication`. Registers a `FhirBridgeSelector` policy scheme that reads (without validating) the incoming bearer token's issuer and forwards to the `Local` HS256 `JwtBearer` scheme or, when `Authentication:Entra:Enabled`, the `Entra` `AddMicrosoftIdentityWebApi` scheme. Entra security groups are projected onto the five built-in roles after token validation. Local scheme validates the symmetric signing key; `roles`/`name` claim types are normalized across both schemes.
- **JwtAccessTokenIssuer** (`IAccessTokenIssuer`) — mints local HS256 access tokens for `LocalAuthService` logins, embedding `oid`, name/email, `pwd_change_required`, `scope`, and role claims; lifetime from `Authentication:TokenLifetimeMinutes` (default 480).
- **HttpContextCurrentUserService** (`ICurrentUserService`) — projects the authenticated `ClaimsPrincipal` into the Application-layer current-user abstraction.
- **UnifiedAdminAuthorizationHandler** / **UnifiedAdminRequirement** — satisfies the `UnifiedAdmin` policy when the principal carries an accepted admin role claim (SuperAdmin/Admin, including the retained legacy claim names).

### Workflows (`Workflows/`)
- **WorkflowEndpoints** — `MapWorkflowEndpoints` registers minimal-API routes under `/api/v1`: `GET workflow-catalog`, `POST/GET/PUT workflows`, `GET workflows/{id}`, and `POST workflows/{id}/{validate|run|activate|deactivate}`. Translates request DTOs into `WorkflowDefinition` aggregates (nodes + edges) and delegates to the Runtime workflow store, validator, and ranked orchestrator.
- **WorkflowApiModels** — request/response records (`WorkflowDefinitionRequest`, node/edge requests, `WorkflowRunRequest`).

### Cross-cutting
- **GlobalExceptionHandlingMiddleware** — catches unhandled exceptions, logs them, and writes a JSON `{ title, status }` body mapping `NotFoundException`→404, `InvalidOperationException`/`ArgumentException`→400, else 500.

## Dependencies
- **Projects:** `FHIRBridge.Application`, `FHIRBridge.Infrastructure`, `FHIRBridge.Domain`, `FHIRBridge.Runtime.Application`, `FHIRBridge.Runtime.Infrastructure`, `FHIRBridge.Observability` (BuildingBlocks).
- **Key packages:**
  - `Asp.Versioning.Mvc` — API versioning for the `V1` controller surface.
  - `Microsoft.AspNetCore.Authentication.JwtBearer` — local HS256 bearer validation.
  - `Microsoft.Identity.Web` — Microsoft Entra ID (Azure AD) bearer authentication when enabled.
  - `Swashbuckle.AspNetCore` — Swagger / OpenAPI generation and UI (dev).
  - `Serilog.AspNetCore` + `Serilog.Sinks.Console` — structured request/application logging.
  - `Microsoft.EntityFrameworkCore.Design` — design-time EF Core support (migrations) for the host project.

## Current state in this skeleton
Only a placeholder `Program.cs` exists (a minimal `WebApplication` host that builds and runs but maps nothing). The real controllers, security pipeline, workflow endpoints, exception middleware, and the full DI composition described above are to be ported from the reference implementation at `…\StepBase\FHIRBridge\src\Api\FHIRBridge.Api`. The project references and NuGet packages in `FHIRBridge.Api.csproj` should match the reference (Application/Infrastructure/Domain/Runtime/Observability + the packages listed above).

## Roadmap — what it will do in detail
1. **Composition root.** Flesh out `Program.cs` to register controllers with `JsonStringEnumConverter`, Swagger/OpenAPI, `HttpContextAccessor`, the current-user/token-issuer/authorization-handler services, and the four layer extension methods (`AddFHIRBridgeApplication`, `AddFHIRBridgeInfrastructure(config)`, `AddWorkflowCore`, `AddWorkflowInfrastructure`). Add dev-only in-memory configuration defaults for the local signing key and seed-admin.
2. **Authentication & authorization.** Port `AddFhirBridgeAuthentication` (the Local/Entra policy-scheme selector and Entra-group→role projection) and register the `UnifiedAdmin` policy backed by `UnifiedAdminRequirement` + `UnifiedAdminAuthorizationHandler`. Wire `app.UseAuthentication()`/`UseAuthorization()`.
3. **REST surface.** Port the V1 controllers, preserving routes/policies:
   - Identity & access: `auth`, `users`, `roles`.
   - Configuration administration: flat `api/v1` configuration CRUD (sources, destinations, mapping profiles, resources, routes, webhooks) — the configuration backbone.
   - Pipeline execution: `pipeline-runs` (sync/queued, bulk-export hand-off), `webhooks/{id}/ingest` (sync/async webhook ingestion).
   - Read/analytics: `fhirbridge/Patient/{id}` aggregation (FHIR Bundle), `insights` (measure reports, anomalies), `lineage`, `observability/metrics`, `audit-logs`.
   - Source capability discovery & catalog gating; FHIR R4 mapping catalog and mapping test.
4. **Workflow designer API.** Map the minimal-API workflow endpoints (catalog, CRUD, validate, run, activate/deactivate) backed by the Runtime workflow store/validator/orchestrator.
5. **Cross-cutting.** Add `GlobalExceptionHandlingMiddleware`, the `Portal` CORS policy (configurable origins, credentials), Swagger UI in development, Serilog request logging, and startup local-identity seeding.
6. **Operational concerns (future).** Health/readiness probes, OpenTelemetry export through `FHIRBridge.Observability`, rate limiting, and tightening the anonymous webhook-ingest endpoint with per-webhook signature/secret verification.
