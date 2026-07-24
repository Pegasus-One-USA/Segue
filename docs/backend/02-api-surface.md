# 02 — API Surface

> Part of the [Backend Architecture Guide](README.md). Reviewed 2026-07-01 (`port/stepbase-overlay`).
> Host: `src/Api/FHIRBridge.Api`. Base path: `/api/v1`.

## Controllers (`Controllers/V1`) + workflow endpoints

| Controller | Route | Highlights |
|---|---|---|
| `AuthController` | `/auth` | `me`, `login`, `internal/login` (anon, local pwd → JWT), `internal/change-password`, `internal/forgot-password` (anon), `internal/reset-password` (anon), `refresh` (anon), `logout` |
| `PermissionsController` | `/permissions` | list all permissions (UnifiedAdmin) |
| `UsersController` | `/users` | CRUD + invite / accept-invite (accept anon) + status + role assign/remove; `HasPermission:user.*` / `role.assign` |
| `RolesController` | `/roles` | role CRUD + permission assign/remove; `HasPermission:role.*` |
| `TenantRegistrationController` | `/register` | **anonymous** self-serve tenant onboarding |
| `TenantConfigurationsController` | `/tenants` | tenant CRUD; source-connections (add/update/deactivate/test); tenant users; webhooks; destinations (+schema); mapping-profiles; resources + routes; `/tenants/import` YAML manifest |
| `MappingController` | `/mapping` | test mapping; catalog resources + fields |
| `PipelineRunsController` | `/tenants/{id}/pipeline-runs` | start (supports bulk), list, deactivate |
| `FhirBridgeAggregationController` | `/tenants/{id}/fhirbridge/Patient/{id}` | synchronous patient read → FHIR searchset Bundle |
| `WebhookIngestionController` | `/tenants/{id}/webhooks/{id}/ingest` | **anonymous**; sync inline or async queue |
| `OAuthController` | `/tenants/.../oauth/*` | `authorize`, `launch` (anon SMART EHR-launch), `/oauth/callback` (anon) |
| `SourceCapabilitiesController` | `/tenants/.../source-connections/{id}` | `capabilities/discover`, `smart-configuration`, get capabilities, `catalog/resources` |
| `SubscriptionsController` | `/tenants/{id}/subscriptions` | register/delete FHIR rest-hook Subscriptions on source |
| `InsightsController` | `/tenants/{id}/insights` | `measure-report` (HEDIS), `anomalies` |
| `ObservabilityController` | `/observability/metrics` | pipeline metrics snapshot (aggregates API + Worker) |
| `WorkflowEndpoints` (minimal API, `Workflows/`) | `/tenants/{id}/workflows` | workflow-catalog, workflow CRUD, validate, run, activate/deactivate |

## Authentication & authorization (`Api/Security` + `Program.cs`)

- **Dual JWT scheme:** **Local HS256** (key `Authentication:SigningKey`) + optional **Microsoft Entra ID**; a selector scheme routes by token issuer (supports mixed local/Entra tenants).
- **`JwtAccessTokenIssuer`** — 60-min access token + 30-day refresh (stored SHA-256 hashed). Embeds roles + permission codes as claims.
- **`HttpContextCurrentUserService`** — reads `sub`/`email`/`roles`/`permissions`/`tenant_id`/`pwd_change_required` from `HttpContext.User`.
- **Two authorization models:**
  - `UnifiedAdmin` policy — requires GlobalAdmin/TenantAdmin; falls back to DB `IUserAccessRepository.HasTenantRoleAsync` (route `tenantId`-scoped). Handler: `UnifiedAdminAuthorizationHandler`.
  - `HasPermission:<code>` policies — one per permission, **auto-registered via reflection over `UnifiedPermissions`** (a new permission constant can never be missed). Handler: `PermissionAuthorizationHandler`.

## Middleware pipeline (order)

1. Global exception handler → maps domain exceptions to HTTP (404 / 400 / 401 / 409 / 500).
2. Swagger + Swagger UI (dev; JWT bearer security definition).
3. Local identity seeding (dev).
4. CORS `"Portal"` (`Portal:AllowedOrigins`, default `localhost:4200`).
5. Authentication.
6. **Password-change-required gate** — blocks `/api/v1/*` (except change-password + me) when `pwd_change_required` claim is true.
7. Authorization.
8. Controllers, then workflow minimal-API endpoints.

DI composition: `AddFHIRBridgeApplication()` + `AddFHIRBridgeInfrastructure(config)` + workflow core/infra + `AddFhirBridgeAuthentication(config, env)`.

## Gateway
`src/Gateway/FHIRBridge.Gateway` references `Yarp.ReverseProxy` but is currently a **stub** — no routes/clusters configured.
