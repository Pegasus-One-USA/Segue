# FHIRBridge — Provider Standalone (App-Held Token) Pass-Through Flow

> **Status:** implemented on branch `feature/provider-standalone-passthrough` (C1–C6). **Date:** 2026-07-02.
> **Guiding constraint:** every change below is **strictly additive**. No existing flow
> (Backend Services, Healow interactive, admin-authorized connections, pipeline runs,
> webhook ingestion, the current `Patient/{id}` aggregation read) changes behavior. New
> optional fields default to `null`; the new code path only runs when the caller opts into it.

---

## 1. Goal

Support the **provider-standalone** pattern where the **third-party provider app** owns the
Epic SMART login:

- The provider app is the Epic SMART client (its own client id, its own redirect URI, public
  client + PKCE).
- Epic's hosted login **and** the patient picker render **inside the app** — the clinician
  logs in and selects the patient there and nowhere else.
- The app exchanges the authorization code, and ends up holding the **Epic access token**, the
  **FHIR base URL**, and the **patient id**.
- The app then calls FHIRBridge, passing that token per request. FHIRBridge **uses** the token
  to fetch from Epic and produce the result. FHIRBridge is **stateless** for this flow — it
  stores no token, runs no OAuth, and is not in Epic's redirect chain.

FHIRBridge stays the Epic OAuth client for the **other** connection types (Backend Services,
Healow, admin-authorized interactive). This document adds a second, parallel path; it does not
replace those.

### 1.1 Sequence

```
Clinician ──▶ Third-party provider app  (SMART auth-code + PKCE, all in the app)
                     │  Epic hosted login + patient picker render in the app
                     │  app exchanges code
                     ▼
             app holds { epicAccessToken, fhirBaseUrl, patientId }
                     │
                     ▼  (Entra-authenticated call to FHIRBridge)
             POST /api/v1/tenants/{tenantId}/fhirbridge/exports
                  { accessToken, fhirBaseUrl, patientId, ehr, include, output }
                     │
                     ▼
             FHIRBridge — uses the supplied token as Bearer, fetches Patient +
             compartment resources via the existing Epic connector, maps them, and
             returns the result in the tenant's admin-configured format. Stores nothing.
```

### 1.2 Functional walkthrough (what each party does)

**One-time setup**

- The org's **admin** registers the **provider app** with Epic (client id + the app's own
  redirect URI). This registration belongs to the app, not FHIRBridge.
- The **admin** configures, in FHIRBridge: which EHRs exist (FHIR base URLs), the field
  **mappings**, and the **output format** they want back (see §2.1).
- The provider app is granted access to call FHIRBridge's API via **Azure Entra**.

**Runtime, from the clinician's point of view**

1. The provider opens the third-party app and taps "Connect to Epic."
2. **Epic's own login page** appears inside the app; the provider signs in. FHIRBridge never
   sees those credentials.
3. Epic shows the **patient picker**; the provider selects a patient.
4. Epic returns the provider to the app, which now holds an **Epic access token** (scoped to
   that provider + patient), the **FHIR base URL**, and the **patient id**.
5. The provider taps "Get data / Export."
6. The app calls FHIRBridge in the background, passing the token, base URL, and patient.
7. FHIRBridge fetches the patient's data from Epic with that token, maps it, and returns the
   result **in the admin-configured format** (or a link, for sink formats).
8. The provider sees the data / receives the file in the app.

Key properties: the login always stays in the app (Epic returns to the app, never to
FHIRBridge); the result is patient-scoped to the picked patient; the token is used once and
never stored; FHIRBridge holds no PHI or secrets between calls.

---

## 2. Responsibility split

| Concern | Owner |
|---|---|
| Epic app registration (client id, redirect URI) | **Provider app** |
| SMART login UI + patient picker | **Epic (hosted), inside the provider app** |
| Authorization-code exchange, holding the Epic token | **Provider app** |
| Caller authentication to FHIRBridge (Entra) | Provider app → **FHIRBridge validates** |
| Fetching FHIR data with the supplied token | **FHIRBridge** |
| Choosing the output format (CSV / JSON / blob / …) | **Admin** — configured in FHIRBridge (§2.1) |
| Mapping FHIR → the configured format | **FHIRBridge** |
| Token persistence / refresh | **Nobody in FHIRBridge** — token is used transiently, per call |

### 2.1 Output format is admin-configured (reuses the existing destination layer)

The admin does **not** hard-code a format in the app; they configure it in FHIRBridge, and the
pass-through endpoint produces whatever format is configured. This reuses the destination layer
that already exists — no new format machinery:

- **`DestinationConfiguration`** (Domain entity) + **`MappingProfile`** — the admin-created
  record of "what format + which fields," per tenant.
- **`DestinationType`** (enum) — the supported formats, already including
  `Csv`, `Ndjson` (JSON lines), `BlobStorage`, `Parquet`, `Avro`, `Pdf`, `Excel`,
  `RestApi`, `S3`, `Sftp`, `Snowflake`, `FhirRepository`, and the SQL targets.
- **`IConfiguredDestinationWriterFactory.Create(type)`** → **`IConfiguredDestinationWriter.WriteAsync(destination, mappingProfile, records, ct)`** — the existing writer that serializes/emits each format.

Two response styles depending on the configured format:

| Configured format | What the app gets back |
|---|---|
| **Inline** (`Csv`, `Ndjson`/JSON, `Pdf`, `Excel`, raw FHIR `Bundle`) | the payload in the HTTP response body |
| **Sink** (`BlobStorage`, `S3`, `Sftp`, `Snowflake`, SQL, `RestApi`, `FhirRepository`) | FHIRBridge writes to the configured sink and returns a reference/link |

The app either passes a `destinationId` (which configured output to use) or omits it to use the
tenant's default. If the tenant has no destination/mapping configured, the endpoint falls back
to returning the raw FHIR `Bundle` (or fails with a clear message — an open decision, §8).

---

## 3. Changes required (all additive)

### C1 — New request DTO *(new file, no impact)*

`src/FHIRBridge.Application/DTOs/PassthroughExportRequest.cs`

```csharp
namespace FHIRBridge.Application.DTOs;

/// <summary>
/// A stateless, caller-token FHIR read: the third-party app supplies the Epic access token it
/// obtained from its own SMART login, plus the FHIR base URL and the patient it selected.
/// FHIRBridge uses the token transiently and stores nothing.
/// </summary>
public sealed record PassthroughExportRequest(
    string AccessToken,          // Epic bearer token held by the app
    string FhirBaseUrl,          // FHIR R4 base URL the token is valid for
    string PatientId,            // the patient the clinician selected
    string Ehr = "epic",         // which connector to use (vendor quirks); Epic first
    string? Include = null,      // "all" | "Observation,Condition,..." (PatientCompartmentResolver)
    Guid? DestinationId = null); // which admin-configured output/format to produce; null = tenant default (§2.1)
```

### C2 — Add an optional access token to `FhirSourceConfiguration` *(additive param)*

[FhirSourceConfiguration.cs](../../src/Runtime/FHIRBridge.Runtime.Application/DTOs/FhirSourceConfiguration.cs) — append one optional, defaulted parameter. Existing
positional callers are unaffected because it has a default and is added last:

```csharp
    ApplicationType? ApplicationType = null,
    // Pass-through: when set, this pre-acquired caller token is used verbatim and NO OAuth is
    // performed. Null for every existing flow, so their behavior is unchanged.
    string? AccessToken = null);
```

### C3 — Guarded short-circuit in the token provider *(one guarded branch)*

[CompositeFhirAccessTokenProvider.cs:45](../../src/Runtime/FHIRBridge.Runtime.Infrastructure/Auth/CompositeFhirAccessTokenProvider.cs#L45) — add a single early return at the **top** of
`GetAccessTokenAsync`, before any existing logic:

```csharp
public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
{
    // Pass-through: the caller already holds a valid token (their app ran the SMART login).
    // Use it verbatim; acquire nothing. Only triggers when the new field is set, so every
    // existing flow falls straight through to the code below unchanged.
    if (!string.IsNullOrWhiteSpace(source.AccessToken))
    {
        return Task.FromResult(source.AccessToken!);
    }

    // …existing application-type + legacy-inference logic, untouched…
}
```

**Why this is safe:** `IFhirAccessTokenProvider` resolves to `CompositeFhirAccessTokenProvider`
([DependencyInjection.cs:71-73](../../src/Runtime/FHIRBridge.Runtime.Infrastructure/DependencyInjection.cs#L71-L73)), and both connector clients
([FhirSourceConnectorBase.cs:58](../../src/Runtime/FHIRBridge.Runtime.Infrastructure/Connectors/FhirSourceConnectorBase.cs#L58) and `FhirRestBulkExportClient`) obtain their bearer
through it. Guarding here covers the whole connector layer from one place, and the guard is
inert unless `AccessToken` is set — no existing caller sets it.

### C4 — New pass-through read service *(new file; does not touch `PatientAggregationService`)*

Rather than modify the existing [PatientAggregationService](../../src/FHIRBridge.Infrastructure/Aggregation/PatientAggregationService.cs) (which resolves a stored
`SourceConnection` and its credentials), add a parallel service that builds the
`FhirSourceConfiguration` **directly from the request** and reuses the shared connector factory,
`ParallelFanOut`, and `PatientCompartmentResolver`.

New abstraction `src/FHIRBridge.Application/Abstractions/Aggregation/IPassthroughPatientReadService.cs`:

```csharp
public interface IPassthroughPatientReadService
{
    Task<PatientAggregationResult> GetEverythingAsync(
        PassthroughExportRequest request,
        IReadOnlyCollection<string> resourceTypes,
        CancellationToken cancellationToken);
}
```

New implementation in `src/FHIRBridge.Infrastructure/Aggregation/` that mirrors the query
construction in `PatientAggregationService` but sets, on each per-type `FhirSourceConfiguration`:

- `SourceType` = mapped from `request.Ehr` (see §5),
- `BaseUrl` = `request.FhirBaseUrl` (drives the search URL — [FhirSourceConnectorBase.cs:53-60](../../src/Runtime/FHIRBridge.Runtime.Infrastructure/Connectors/FhirSourceConnectorBase.cs#L53-L60)),
- `AccessToken` = `request.AccessToken` (triggers C3),
- no `ClientId` / `ClientSecret` / `PrivateKeyPem` / `TokenEndpoint` (none needed — no OAuth).

It calls `_sourceClientFactory.Create(sourceType).SearchAsync(...)` exactly like the existing
service, so retries/pagination/throttling are inherited unchanged.

### C5 — New endpoint *(new controller; existing controllers untouched)*

`src/Api/FHIRBridge.Api/Controllers/V1/FhirBridgePassthroughController.cs`, Entra-authorized,
tenant-scoped for auditing consistency with [FhirBridgeAggregationController](../../src/Api/FHIRBridge.Api/Controllers/V1/FhirBridgeAggregationController.cs):

```csharp
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]   // caller (the app) via Entra — see C7
[Route("api/v1/tenants/{tenantId:guid}/fhirbridge")]
public sealed class FhirBridgePassthroughController : ControllerBase
{
    [HttpPost("exports")]
    public async Task<IActionResult> Export(Guid tenantId, [FromBody] PassthroughExportRequest request, CancellationToken ct)
    {
        var types = PatientCompartmentResolver.Resolve(request.Include);
        var result = await _passthroughRead.GetEverythingAsync(request, types, ct);
        // phase 1: return FhirBundleBuilder.Build(...) as application/fhir+json  (reuse existing helper)
        // phase 2 (Output == "csv"): map + MappedCsvDestinationWriter, return the CSV / a link
        // reuse the PHI-free DataAccess audit pattern from FhirBridgeAggregationController
    }
}
```

Register `IPassthroughPatientReadService` in the Infrastructure DI module (additive line).

### C6 — Output in the admin-configured format *(reuses the destination layer)*

The endpoint produces whatever format the admin configured (§2.1) — it does not hard-code CSV:

1. Resolve the tenant's `DestinationConfiguration` + `MappingProfile` (by `request.DestinationId`,
   or the tenant default).
2. Map the fetched resources to `MappedDestinationRecord`s (reuse the existing mapping).
3. Hand them to `IConfiguredDestinationWriterFactory.Create(destination.Type)` →
   `WriteAsync(destination, mappingProfile, records, ct)` — the same writers the configured
   pipeline uses (`Csv`, `Ndjson`, `BlobStorage`, `Parquet`, `Pdf`, `Excel`, `S3`, …).
4. **Inline** formats return the payload in the response body; **sink** formats write to the
   configured target and return a reference/link (see the table in §2.1).

Fallback: if no destination/mapping is configured for the tenant, return the raw FHIR `Bundle`
(reusing `FhirBundleBuilder`) so the endpoint is usable before mappings are set up.

### C7 — Caller authentication (Entra) *(config; shared — apply with care)*

The provider app must authenticate to FHIRBridge via **Azure Entra** (already scaffolded, off by
default — [FhirBridgeAuthenticationExtensions.cs:47-80](../../src/Api/FHIRBridge.Api/Security/FhirBridgeAuthenticationExtensions.cs#L47-L80), [appsettings.json](../../src/Api/FHIRBridge.Api/appsettings.json)). Enabling
Entra is a shared setting that affects all callers, so validate it against existing consumers
before turning it on in an environment. If the new endpoint should require an app (not user)
identity, gate it on an app-role/scope claim rather than the interactive-admin policy.

---

## 4. What is explicitly NOT changed

- `OAuthController`, `InteractiveSourceAuthorizationService`, the OAuth state store, and the
  token stores — untouched; the Backend Services, Healow, and admin-authorized interactive flows
  keep working exactly as today.
- `PatientAggregationService` and the existing `GET .../fhirbridge/Patient/{id}` route — untouched.
- `PipelineRunsController`, `WebhookIngestionController`, all configuration controllers — untouched.
- The connector layer, retry/pagination/throttle, and mTLS on outbound calls — untouched
  (the pass-through path reuses them as-is).
- `FhirSourceConfiguration` existing fields, and every existing branch of
  `CompositeFhirAccessTokenProvider` — untouched (only a new field and a guarded early return).

---

## 5. EHR → connector mapping

`request.Ehr` maps to `RuntimeSourceType` the same way the existing services do
(`PatientAggregationService` mapping / `FhirSourceClientFactory` registry). Epic and Sample are
enabled today; others are gated. For this flow, start with:

| `ehr` | `RuntimeSourceType` | Enabled |
|---|---|---|
| `epic` | `Epic` | yes |
| `sample` | `Sample` | yes (tests) |

Add vendors by enabling them in the connector factory registry — no change to this endpoint.

---

## 6. Security requirements

- **Never log the token.** The Epic access token arrives in the request body; ensure request
  logging/middleware does not capture bodies for this route, and never write the token to logs
  or audit (follow the metadata-only pattern already used by `FhirAccessTokenAuditSink`).
- **Transient use only.** The token lives for the duration of the call. Do not cache or persist
  it (this flow deliberately bypasses the token stores).
- **TLS.** Require HTTPS inbound for this endpoint (add `UseHttpsRedirection`/`UseHsts` if not
  already enforced) so the token is never sent in clear text. Outbound Epic calls already use
  mTLS/TLS via the shared connector `HttpClient`.
- **Audit.** Emit the same PHI-free `DataAccess` audit event the aggregation controller emits
  (who/what/when/patient logical id/outcome) — never resource content, never the token.

---

## 7. Test checklist

- Existing flows regression: a Backend Services source and a Healow interactive source still
  acquire tokens and read data (proves C2/C3 are inert when `AccessToken` is null).
- Pass-through happy path: given a valid token + base URL + patient id, `POST .../exports`
  returns the expected Bundle; the connector sent `Authorization: Bearer <supplied token>` and
  performed no token-endpoint call.
- Token not persisted: after a pass-through call, nothing is written to the token store or
  session store.
- Bad/expired token: Epic 401 surfaces as a clean error (no retry storm, no token leak in the
  message).
- Unauthenticated caller is rejected (Entra required).
- Token never appears in logs/audit.

---

## 8. Open decisions

1. **One patient vs. many.** This design fixes the patient per request (the app passes the
   patient it selected). If the app should query multiple patients on one token, the endpoint
   already supports that — each call passes a different `patientId`; nothing else changes.
2. **Format fallback behavior.** When a tenant has no destination/mapping configured, should the
   endpoint return the raw FHIR `Bundle` (usable out of the box) or fail asking the admin to
   configure an output format first? Also confirm which formats must ship first (Csv + Ndjson
   are the likely minimum for inline responses).
3. **Endpoint auth claim.** Decide whether the app authenticates as an app-role/scope (preferred
   for machine-to-machine) vs. reusing the current admin policy (C7).
