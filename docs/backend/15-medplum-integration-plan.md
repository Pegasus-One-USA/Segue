# 15 — Medplum Integration Plan

**Status:** research complete (Medplum docs read in full, Aug 2026); design proposed; no code written.
**Goal:** integrate **eClinicalWorks (healow/eCW) and Epic** *with* **Medplum** using FHIRBridge as the bridge —
i.e. move clinical data from the Epic/eCW SMART-on-FHIR sources FHIRBridge already speaks into Medplum as a
FHIR-native store, with optional read-back and change-driven sync. Covers all four requested roles: Medplum as a
**destination writer**, a **source connector**, a **backing FHIR store**, and an **auth/identity** provider.

> Sourced entirely from `medplum.com/docs`. Where the docs were silent, it is flagged as a POC/verify item (see §12).
> This is a plan, not a spec — the code sketches show intended shape, not final signatures.

---

## 1. What Medplum is (framing)

- **A FHIR-native "headless EHR" / developer platform**, Apache-2.0 open source (`github.com/medplum/medplum`, operated
  by Orangebot, Inc.). Backend + SDK + optional React UI + serverless "Bots".
- **Everything clinical is stored as standard FHIR R4 (4.0.1) resources** (150+ types), validated against Profiles /
  US Core (USCDI). No proprietary shredded model — so FHIRBridge's existing FHIR mapping applies 1:1.
- **Deployment:** hosted SaaS (`app.medplum.com` / `api.medplum.com`) **or** self-host (AWS recommended; Azure + GCP Beta; Docker).
- **Two UIs:** *Medplum App* (`app.medplum.com`, admin/dev console — where you provision `ClientApplication`
  credentials and inspect data) and *Medplum Provider* (a clinician EHR front end). Both are React/Mantine — **not**
  directly reusable in FHIRBridge's Angular 20 portal (reference only).
- **Key constraint to design around:** resources are searchable **only by predefined FHIR search parameters** per type,
  and writes are validated against profiles.

## 2. Target architecture

FHIRBridge stays the ETL/bridge front end; Medplum becomes the central FHIR store.

```
   Epic  ─┐  (SMART Backend Services / RS384 — already built)      ┌─→ read-back / downstream sync (optional)
          ├─────────────►  FHIRBridge (ETL)  ─── writes FHIR ──────┤
  eCW/    ─┘  (auth-code + PKCE — already built)   normalize +      └─→  Medplum  ◄─ central FHIR R4 store
  healow                                            govern + map          │  (Project = tenant boundary)
                                                                          │
                          FHIRBridge webhook  ◄──── Subscription ─────────┘  (rest-hook + HMAC, change capture)
```

The four roles map onto this one topology:

| Requested role | What it means here | Build size |
|---|---|---|
| **Source connector** (Epic, eCW) | *Already built.* Epic = SMART Backend Services (RS384 `private_key_jwt`); eCW/healow = auth-code + PKCE. No new work beyond config. | none / config |
| **Destination writer** (→ Medplum) | **The primary new build.** A `MedplumDestinationWriter` that upserts FHIR into Medplum. | medium |
| **Backing FHIR store** | Medplum *is* the datastore the bridged data lands in; one Medplum `Project` per FHIRBridge tenant. | config + modeling |
| **Source connector** (Medplum → out) | Optional: read FHIR back out of Medplum (like a generic-FHIR source) for downstream fan-out or verification. | small (reuses generic-FHIR) |
| **Auth/identity** | Medplum as SMART/OAuth2 authorization server; FHIRBridge authenticates as a `ClientApplication`. | reuses existing strategy |

## 3. Auth — the cleanest fit with what FHIRBridge already has

Medplum is a full SMART App Launch 2.0.0 authorization server. A bridge connector authenticates as a
**`ClientApplication`** (a machine principal, provisioned in the Medplum App at `/admin/clients`), scoped to one
**`Project`**. Two backend flows are supported — **both already implemented in FHIRBridge's `ApplicationType`
strategy registry**, so this is config, not a new auth axis (no `switch` changes, per the architecture rule):

| Flow | Maps to FHIRBridge | Notes |
|---|---|---|
| **`client_credentials`** (client_id + client_secret) | Healow / generic-FHIR backend strategy | Recommended M2M. |
| **`private_key_jwt` / JWT client assertion (JWKS)** | Epic's `BackendServicesApplicationStrategy` (RS384) | Medplum accepts ES256/384/512, **RS256/384/512** → RS384 works as-is. |

**Token endpoint (both flows):** `POST {base}/oauth2/token`, `Content-Type: application/x-www-form-urlencoded`.

```
grant_type=client_credentials
&client_id=<ClientApplication UUID>
&client_secret=<secret>              # client_credentials flow
# — or —
&client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
&client_assertion=<signed JWT>       # JWKS flow
```

Response: `{ "token_type": "Bearer", "access_token": "<jwt>", "expires_in": 3600 }`.

- **1-hour tokens, no refresh token** → re-request on expiry. Slots straight into `DistributedFhirAccessTokenCache`
  exactly like the Epic/Healow flows.
- **Gotcha:** the JWKS flow still sends `grant_type=client_credentials` (the assertion, not the grant_type, signals
  JWT-bearer). And it requires a **hosted HTTPS JWKS endpoint** (`jwksUri` field on the `ClientApplication`), not a
  static uploaded key. FHIRBridge already generates signing keys — expose the public JWKS at a URL Medplum can fetch.
- **Permissions come from the `AccessPolicy`** attached to the ClientApplication's `ProjectMembership`, **not** from
  OAuth scopes. The connector's token silently sees only what its policy allows — grant `create`/`update` on the
  target resource types, and `AsyncJob` for bulk ops (see §5/§6).
- **Base URL must be configurable** — hosted `https://api.medplum.com` vs self-hosted `https://<host>` (dev `:8103`).
  Do **not** hardcode `api.medplum.com`.

## 4. Tenant / Project model → maps onto FHIRBridge `Tenant`

```
Project (isolation boundary ≈ FHIRBridge Tenant)
  └─ ProjectMembership ── principal: User | ClientApplication | Bot
                       ── profile:   Patient | Practitioner | RelatedPerson (human) / ClientApplication (machine)
                       ── access[]:  AccessPolicy (+ per-membership parameters)
```

- **One Medplum `Project` per FHIRBridge tenant-source**; **one `ClientApplication` per Project**. Store the
  per-tenant credentials the same way FHIRBridge already stores source/destination secrets (Key Vault refs).
- `User` is **global** (spans Projects); `ProjectMembership` is the join that scopes an identity into a tenant.
- **`AccessPolicy`** does resource- and field-level gating via a `criteria` FHIR search string, `readonly`/
  `hiddenFields`/`readonlyFields`, `interaction[]` (create/read/update/delete/search/history/vread), FHIRPath
  `writeConstraint`, and reusable `%parameter` substitution per membership. **Caveat:** policy `criteria` does **not**
  support chained search — only `:not` and `:missing`.

## 5. Medplum as a destination writer (the primary build)

The write path is **idempotent by design** — this is the big win and removes the ID-remap table FHIRBridge would
otherwise need for write-back.

- **Preserve Epic/eCW primary keys as FHIR `identifier`s**, e.g.
  `{ "system": "http://epic/patientId", "value": "..." }`.
- **Upsert by identifier via conditional update:** `PUT {base}/fhir/R4/Patient?identifier=http://epic/patientId|P001`
  with the resource body → 0 matches = create, 1 = update, 2+ = error. (Conditional create alt:
  `POST` with header `If-None-Exist: identifier=<system>|<value>`.)
- **Conditional references** avoid ordering/lookup entirely: `{ "reference": "Patient?identifier=http://epic/patientId|P001" }`
  — Medplum resolves the search at write time, so resources can be written without knowing Medplum-assigned IDs.
- **Batch for throughput:** `POST {base}/fhir/R4` with a `Bundle` (`type: "batch"` = independent entries, best
  throughput; `type: "transaction"` = atomic but capped at **50 update ops**, and **8 entries** when using
  conditional ops under serializable isolation).
- **Concurrency:** standard `ETag` / `If-Match: W/"<versionId>"` on `PUT` for safe updates.

**Hard limits the writer must respect:**

| Constraint | Value |
|---|---|
| Sync request body / timeout | **8 MB / 60 s** |
| Async body (`Prefer: respond-async`) | **50 MB**, no timeout, background |
| Transaction bundle | ≤ **50** update ops (≤ **8** if conditional) |
| Search `_count` | default 20, **max 1000** |

**Rate limits — writes are the bottleneck (design around this):**
- Per-IP request cap: **6,000/min free, 60,000/min paid**. Auth endpoints far lower (login 5/min, `/oauth2/*` 160/min).
- Weighted **`fhirInteractions`** quota (per user/min; project = 10×): read=1, search=20, history=10,
  **create/update/delete/patch = 100 each**. A batch's cost = sum of entry costs → 100 creates = 10,000 points. On
  free tier a naive create-heavy load caps ~500 writes/min.
- **Escape hatch:** `Prefer: respond-async` batch bundles **do not consume the interaction quota** (only status
  polls / Binary reads cost 1 each). **Route all bulk/migration writes through the async path.** Async returns
  **202** + `Content-Location`; poll `GET .../job/{id}/status` (202 = keep polling, 200 = done → a `Binary` holding
  the response Bundle). Requires `AsyncJob` access in the policy. (Async applies to **batch**; async *transaction*
  bundles are rejected when `transaction-bundles` is enabled.)
- Read the `RateLimit` header (`"requests";r=..;t=.., "fhirInteractions";r=..;t=..`) and throttle proactively; on
  **429**, wait the indicated `t` seconds — no blind retry.

**Design:** a `MedplumDestinationWriter` alongside the existing 21 writers → map to FHIR R4 → stamp source
identifiers → emit **async batch Bundles of conditional PUTs** → throttle on the `fhirInteractions` budget. Fully
idempotent; reuses FHIRBridge's retry/optimistic-concurrency model.

## 6. Medplum as a source connector (read-back — optional)

Medplum is a near-vanilla FHIR R4 server, so this reuses FHIRBridge's **generic-FHIR** source patterns.

- **Base:** `{base}/fhir/R4`, `application/fhir+json`, `Authorization: Bearer`.
- **Initial / bulk read:** **Bulk Data 2.0.0 `$export`** (NDJSON) — `GET {base}/fhir/R4/$export` (system) or
  `Group/{id}/$export`, with `Accept: application/fhir+json`, `Prefer: respond-async` → 202 + status URL → poll to
  200 → `output[]` of `{type, url}` NDJSON files. Filters: `_type=`, `_since=`, `_outputFormat=ndjson`. Bypasses
  pagination + interaction quota.
- **Incremental sync:** `GET {base}/fhir/R4/{type}?_lastUpdated=gt<checkpoint>&_sort=_lastUpdated&_count=1000`,
  then follow `Bundle.link.next` (**cursor** pagination — scales to millions; always follow `next`, never build
  `_offset`, which caps at 10,000). Persist max `meta.lastUpdated` as the next checkpoint. Fits FHIRBridge's existing
  per-resource-type sync-cursor work.
- **Other:** GraphQL (`POST {base}/fhir/R4/$graphql`) for element-projected reads; `$everything`, `$validate`,
  Terminology (`$expand`, `$validate-code`, `$translate` — LOINC/SNOMED/RxNorm/ICD-10 loaded) can back FHIRBridge's
  normalization/terminology step.

## 7. Change capture out of Medplum (Subscriptions)

Best fit for FHIRBridge's webhook-triggered pipeline runs: **FHIR R4 `Subscription`, `rest-hook` channel**.

```json
{ "resourceType": "Subscription", "status": "active",
  "reason": "Push Patient changes to FHIRBridge",
  "criteria": "Patient",
  "channel": { "type": "rest-hook",
    "endpoint": "https://segue.pegasusone.com/webhooks/medplum",
    "payload": "application/fhir+json",
    "header": ["Authorization: Bearer <static token>"] } }
```

- Fires on **create + update** by default; POSTs the changed resource as `application/fhir+json` (empty `payload` =
  ping → consumer re-fetches).
- **Secure the callback:** set the `subscription-secret` extension → Medplum sends
  **`X-Signature` = hex HMAC-SHA256** over the raw body. Verify in .NET with `HMACSHA256` over raw request bytes. Static
  bearer via `channel.header` also supported.
- **Tuning extensions:** `subscription-supported-interaction` (create/update/delete),
  `fhir-path-criteria-expression` (`%previous`/`%current` FHIRPath filter), `subscription-max-attempts` (1–18,
  default 4). Retry = exponential backoff w/ jitter, ~20s base, capped 8h; manual `$resend` exists.
- **Idempotency required** — at-least-once delivery; dedupe on resource id + `versionId`.
- **Alternatives:** a **Bot** (subscription/cron/HTTP-triggered TS function) can transform + `fetch()`-POST to
  FHIRBridge — use only when reshaping/fan-out is needed (**note: Bot-targeted subscriptions do not retry**).
  **WebSocket** subscriptions avoid exposing an inbound endpoint but are less documented. FHIRcast (UI-context sync),
  the on-prem Agent (HL7v2/DICOM on-ramp), and CDS Hooks are **not** change-capture paths.

## 8. HL7 v2 — avoid duplication

Medplum's **on-prem Agent + Bots** are positioned as an HL7 interfacing engine (MLLP, "alternative to Mirth/Corepoint"),
which **overlaps FHIRBridge's own `Hl7MllpListenerService` + ADT/ORU/MDM→FHIR mapper**. Recommended pattern:
**FHIRBridge remains the HL7 engine; Medplum is a FHIR sink.** Do **not** run the Medplum Agent — it duplicates the
MLLP listener. (C-CDA convert lib `@medplum/ccda` is TS-only; a .NET path would call it via a Bot or build its own.)

## 9. Deployment

- **Fastest path: hosted Medplum** (`api.medplum.com`) — HIPAA/SOC2/HITRUST/ONC posture handled by Medplum; you get a
  Project + ClientApplication and the FHIR API with no infra. **Start here.**
- **Self-host stack (all modes):** Node API server + **PostgreSQL 16** + **Redis** + object storage (S3/Blob) + CDN.
  Min 4 GB RAM/disk; server port 8103.
- **Azure self-host is possible but Beta** (relevant since Segue is Azure-oriented): AKS + Azure DB for PostgreSQL
  Flexible Server + Azure Cache for Redis + Blob + CDN + App Gateway + Key Vault, via Terraform + Helm. Docs call it
  "validated for production" but "requires significant operational expertise." Treat as a later decision, not the POC.
- **Compliance:** shared-responsibility — request a **BAA** (`hello@medplum.com`) before real PHI. Medplum emits FHIR
  `AuditEvent` resources (a natural thing for FHIRBridge to pull). Self-hosting shifts the compliance burden to us.

## 10. .NET integration surface

**No official .NET SDK** — the integration path is the **raw FHIR R4 REST API + OAuth2**, which FHIRBridge's
`FhirSourceConnectorBase` / destination-writer model already targets. Behaviors worth replicating from the TS
`MedplumClient`: proactive token refresh before `exp` (→ existing token cache), 429-aware backoff honoring the
`RateLimit` header, `ETag`/`If-None-Match` conditional requests, and transaction-bundle reference reordering
(`urn:uuid:` linking). The `@medplum/cli` (`bulk export` / `bulk import` NDJSON, `bot deploy`) is handy for ops/CI
seeding but not usable in-process.

## 11. Phased build plan

1. **Spike (hosted, POC):** create a free Medplum Project + ClientApplication; from a throwaway .NET client, do
   `client_credentials` → token → `PUT Patient?identifier=...` upsert → read back. Confirms auth + write + idempotency
   end-to-end. Measure the real `fhirInteractions` budget (see §12).
2. **Auth wiring:** register Medplum as a backend source/destination using the existing `ApplicationType` strategies
   (client_credentials, and JWKS/RS384 with a hosted JWKS URL). Configurable base URL. Token caching via
   `DistributedFhirAccessTokenCache`.
3. **`MedplumDestinationWriter`:** map → stamp source identifiers → **async batch Bundles of conditional PUTs** →
   `RateLimit`-aware throttle → 429 backoff. This is the core "Epic/eCW → Medplum" flow.
4. **Tenant modeling:** one Project per tenant; author an `AccessPolicy` granting create/update on target types +
   `AsyncJob`; store per-tenant credentials.
5. **Change capture (if needed):** FHIRBridge inbound webhook + HMAC verify; provision `Subscription` resources in
   Medplum per resource type.
6. **Read-back (if needed):** Medplum-as-source via generic-FHIR patterns — `$export` initial + `_lastUpdated` cursor
   incremental.
7. **Deployment decision:** stay hosted, or plan Azure self-host (Beta) — with BAA in place before PHI.

## 12. Open questions to verify in the POC (docs were silent)

- **Exact default `fhirInteractions` point budget/min** (only per-op weights + per-IP caps are published) — measure
  against a live project; it drives write-throughput planning.
- **Enumerated US Core profile list** and how strict write-time profile validation is (affects mapping completeness).
- **Bot outbound `fetch()` egress policy / allowlist** on self-hosted (if Bots are used for change capture).
- **WebSocket subscription** `$get-ws-binding-token` flow + delivered Bundle format for a non-browser .NET client.
- **`.well-known/smart-configuration`** scope catalog for any interactive/auth-provider use.

## 13. Sources

Medplum docs (read Aug 2026): [Overview](https://www.medplum.com/docs) ·
[Auth](https://www.medplum.com/docs/auth) · [Client credentials](https://www.medplum.com/docs/auth/methods/client-credentials) ·
[Access policies](https://www.medplum.com/docs/access/access-policies) · [User management](https://www.medplum.com/docs/user-management) ·
[API](https://www.medplum.com/docs/api) · [FHIR datastore](https://www.medplum.com/docs/fhir-datastore) ·
[Batch requests](https://www.medplum.com/docs/fhir-datastore/fhir-batch-requests) ·
[Async bundles](https://www.medplum.com/docs/fhir-datastore/processing-async-bundles) ·
[Search](https://www.medplum.com/docs/search) · [Paginated search](https://www.medplum.com/docs/search/paginated-search) ·
[Bulk $export](https://www.medplum.com/docs/api/fhir/operations/bulk-fhir) · [Subscriptions](https://www.medplum.com/docs/subscriptions) ·
[Subscription extensions](https://www.medplum.com/docs/subscriptions/subscription-extensions) ·
[Bots](https://www.medplum.com/docs/bots) · [Migration](https://www.medplum.com/docs/migration) ·
[SMART App Launch](https://www.medplum.com/docs/integration/smart-app-launch) · [HL7 interfacing](https://www.medplum.com/docs/integration/hl7-interfacing) ·
[Agent](https://www.medplum.com/docs/agent) · [C-CDA](https://www.medplum.com/docs/integration/c-cda) ·
[Rate limits](https://www.medplum.com/docs/rate-limits) · [Self-hosting](https://www.medplum.com/docs/self-hosting) ·
[Compliance](https://www.medplum.com/docs/compliance) · [Terminology](https://www.medplum.com/docs/terminology) ·
[GraphQL](https://www.medplum.com/docs/graphql)
