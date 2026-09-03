# eCW (eClinicalWorks / Healow) Backend Bulk Group `$export` — Integration Plan

Companion to [16-athena-backend-bulkexport-integration-plan.md](16-athena-backend-bulkexport-integration-plan.md).
That doc established that FHIRBridge's bulk-export machinery is generic to the HL7 FHIR Bulk Data
IG and that the only per-vendor work is GroupId shape, concurrency limits, and error-text matching.
This doc applies that finding to eCW and records what the standalone Python/Flask POC proved.

---

## 1. Headline

The machinery is already built and is vendor-neutral. eCW backend bulk export is **smaller** than
Athena's was, for two reasons:

1. **eCW's GroupId is a raw UUID, used verbatim.** No `a-1.C-{Practice}`-style formatting hook is
   needed — `FhirRestBulkExportClient.BuildKickOffUrl` already passes `request.GroupId` straight into
   `Group/{id}/$export`, which is exactly what eCW wants.
2. **The one unverified integration point is now verified.** eCW's backend auth is SMART Backend
   Services (`client_credentials` + `private_key_jwt`, **RS384**). FHIRBridge's
   `BackendServicesApplicationStrategy` → `EpicAccessTokenProvider` path is vendor-neutral and already
   does exactly this — but it had never been round-tripped against eCW. The POC (see §2) proved eCW
   accepts it end to end: token → `Group/{id}/$export` → poll → NDJSON.

So this is primarily a **wiring + verification** job, not new machinery.

---

## 2. What the POC proved (de-risks the auth unknown)

Standalone Python/Flask POC at `C:\Users\Administrator\Downloads\eCW_BackendSearch-2` (headless
`probe.py` + Flask `app.py`). Verified **2026-09-01** against `staging-fhir.ecwcloud.com/fhir/r4/FFBJCD`:

| Stage | Result |
|---|---|
| Token | `200` — RS384 `private_key_jwt` assertion accepted, all 10 `system/*.read` scopes granted (clientName "Backend Bulk FK", workflow `backend`, vendorId 4998) |
| List groups | `200` — group visible via `GET /Group` |
| Kick off | `GET Group/4d31bd70…/$export?_type=Patient` + `Prefer: respond-async` → `202` + `Content-Location` |
| Poll | 5× `202` (`X-Progress: 0%`) over ~103s, then `200` + manifest |
| Download | `Patient-1.ndjson`, 3 resources, 7,909 bytes |

eCW API behaviours confirmed by the POC that inform this plan:

- **Group-level `$export` only.** No system-level or patient-level export in eCW's docs.
- **Token endpoint is a different host** from the FHIR base — discovered from the CapabilityStatement
  `oauth-uris` extension (`staging-oauthserver.ecwcloud.com/oauth/oauth2/token`). FHIRBridge discovers
  this the same way, so no hardcoding needed.
- **RS384 only** (not RS256/ES384). `BackendServicesJwtFactory` already signs RS384.
- **`Group.identifier[0].value` (portal UUID) ≠ `Group.id`.** `$export` wants the identifier. This is
  the value the operator pastes into the source's Group ID field; it is used verbatim.
- **Duplicate kick-off returns HTTP 200 + `OperationOutcome`/`code: duplicate`**, not a 4xx — status
  alone cannot detect it; the payload must be inspected.
- **The `Content-Location` poll URL is the only handle on a job.** eCW has no endpoint that lists
  running jobs; lose the URL and the job is unrecoverable until it ages out. FHIRBridge already
  persists it (`BulkExportJob.StatusUrl`) before anything else can fail.
- **Introspection is unreliable** (`active:false` for a working token) — treat only `active:true` as
  meaningful. Not on the FHIRBridge path (FHIRBridge tests a token with a real FHIR call), so no action.
- **Tokens last 300s.** FHIRBridge's token cache + provider re-mints, so long polls/downloads survive.

---

## 3. Reuse map (POC → FHIRBridge, all already built)

| POC piece | FHIRBridge equivalent |
|---|---|
| `assertion.py` — RS384 `client_assertion` | `Auth/BackendServicesJwtFactory.cs` (`IBackendServicesJwtFactory`) |
| `auth.py` — token endpoint discovery + `client_credentials` exchange | `Auth/EpicAccessTokenProvider.cs` (vendor-neutral private_key_jwt) |
| Backend auth axis | `Applications/BackendServicesApplicationStrategy.cs` (registry, no switch) |
| `bulk.py` — kickoff / poll / manifest / NDJSON / DELETE | `Connectors/FhirRestBulkExportClient.cs` (`IFhirBulkExportClient`) |
| `jobstore.py` — poll-URL ledger | `BulkExportJob` entity + `BulkExportPollWorker` + `BulkExportPollService` (durable, DB-backed) |
| eCW connector (search REST) | `Connectors/EClinicalWorksFhirSourceClient.cs` (interactive PKCE today) |
| JWKS publishing | `Sources/SourceJwksService.cs` + `Api/Controllers/V1/SourceJwksController.cs` |

Token dispatch funnel: `Auth/CompositeFhirAccessTokenProvider.cs` resolves by `source.ApplicationType`
via `SourceApplicationStrategyRegistry` — so a Healow source with `ApplicationType=Backend` +
`PrivateKeyPem` automatically routes to `BackendServicesApplicationStrategy` → RS384 JWT. No new
auth code.

Bulk-vs-search branch: `Workflows/Executors/SourceNodeExecutors.cs` — when
`source.RetrievalMethod == "bulk-export"` and a `IBulkExportJobRepository` is wired, it kicks off once,
persists a `BulkExportJob(..., SourcePath=WorkflowNode, ...)`, and defers to the poll worker. eCW Group
export follows this exact path.

---

## 4. Deltas to implement

Small and additive. No architecture-test-banned switches.

### 4.1 Portal — enable the Backend audience for eCW  *(required, 1 line)*
`portal/src/app/components/epic-source-wizard/models/audience-field-config.data.ts:57` —
remove `'backend-system'` from the `Healow` entry of `VENDOR_DISABLED_AUDIENCES`
(leaving `['provider-standalone']`). The comment there already says "re-enable one-by-one as each is
verified"; the POC is that verification for Backend.

### 4.2 Confirm auth routing end-to-end against eCW  *(verification, the real deliverable)*
A Healow `SourceConnection` saved with `ApplicationType=Backend`, a `PrivateKeyPem`/`KeyId`, the eCW
`ClientId`, and the eCW token endpoint must:
1. Resolve through `SourceConnectionRuntimeResolver` into a `FhirSourceConfiguration`.
2. Acquire a token via `CompositeFhirAccessTokenProvider → BackendServicesApplicationStrategy →
   EpicAccessTokenProvider` (RS384).
3. Publish the app's JWKS via `SourceJwksController` and register that URL on the eCW dev portal so the
   `kid` matches (the POC's default `kid` = RFC 7638 thumbprint; FHIRBridge's `SigningKeyGenerationService`
   /`SourceJwksService` must expose the same `kid` in the published set).

Verify token discovery picks up eCW's separate `oauthserver` host from the CapabilityStatement
`oauth-uris` extension (it does for the POC; confirm FHIRBridge's discovery does too, or set the token
endpoint explicitly on the source).

### 4.3 Group export config on the eCW source  *(config, no code)*
Set `RetrievalMethod="bulk-export"`, `ExportScope="group"`, and `GroupId=<portal Group UUID>` on the
source. `SourceNodeExecutors` + `FhirRestBulkExportClient.BuildKickOffUrl` already emit
`Group/{id}/$export` verbatim and defer to `BulkExportPollWorker`. No GroupId formatting hook is
required (unlike Athena).

### 4.4 eCW-specific quirks  *(add only if a real run needs them)*
- **Duplicate kick-off = HTTP 200 + OperationOutcome/duplicate.** Confirm
  `FhirRestBulkExportClient`/`BulkExportKickOffFailureDiagnosisRule` treats a `200` carrying an
  `OperationOutcome` with `code=duplicate` as a failure (not success). If it currently trusts the `202`
  status only, add a payload check — the POC's `is_duplicate_job` is the reference behaviour.
- **Concurrency limits.** eCW enforces one running job per group (a duplicate is rejected). If we want to
  pre-empt this rather than react to the 200/duplicate, add eCW's limit as a per-source-type value the
  Athena way (additive config, never a switch). Defer unless a real run hits it.
- **`SourceCapabilityDiscoveryService`** throws for non-Epic. eCW backend export does not depend on
  capability discovery, so no change unless the portal wizard calls it for Healow.

---

## 5. What the user/operator sees

- **eCW "Backend System" audience becomes selectable** in the source wizard (today it's greyed out).
- Configuring a Healow Backend source with a Group-scoped, bulk-export route and triggering it kicks off
  a real eCW `Group/{id}/$export`, defers, and resumes automatically when the export completes — the
  NDJSON flows into the pipeline like any other source.
- No behaviour change for Epic/Athena or eCW's existing interactive (Patient / EHR-launch) modes.

---

## 6. Order of work

1. **4.1** — flip the portal audience flag.
2. **4.2** — stand up an eCW Backend source in a running FHIRBridge (API + Worker), publish/register JWKS,
   and confirm a token is minted against eCW. This is the highest-value step; the POC proves eCW's side,
   this proves FHIRBridge's side reaches the same result.
3. **4.3** — configure the Group route and run it end-to-end; confirm a `BulkExportJob` is created, polled,
   and completes with NDJSON in the pipeline.
4. **4.4** — add the duplicate-200 guard if not already present; defer concurrency pre-emption.

Sandbox for verification (from the POC, VPN + corporate MITM proxy in play — see the
`ecw-backend-bulk-poc` memory): FHIR base `https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD`,
client_id `e79bVBF05z0Zv5B7GU0usrS7A-IxPb_sBQBlBnR_OZg`, small working group
`4d31bd70-75db-4e56-81de-44eb7af2a4a5` (MedChartScan LLC, 3 patients). The configured group
`042c81a3-…` is blocked by a stuck duplicate job — use the small one for round-trip tests.
