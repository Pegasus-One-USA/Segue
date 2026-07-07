# Workflow Builder → Option B — Continuation / Handoff

**Branch:** `feature/workflow-persistence` (repo `1. Projects\FHIRBridge`, remote Pegasus-One-USA). All changes below are **uncommitted** on this branch.

This doc lets a fresh session continue the "builder-authored workflows that reference real connections, save-create the entities, and launch by workflowId" feature. Read the memory file `fhirbridge-workflow-persistence.md` too — it has the full arc.

## Product decisions (locked)
- **Option A** (reference connections by id) is the base; **Option B** (Save also *creates* the source/destination/mapping records) is being built on top.
- **Contract relaxation = option 1**: catalog `MappingNode` accepts `ResourceBatch`/`NormalizedResourceBatch`/`DeIdentifiedBatch` (DeId optional; destination still requires a mapping upstream). DONE.
- **Secrets = B1**: app provisions secrets into a new **encrypted `ProvisionedSecrets`** table (DataProtection). DONE.
- Merge nodes: flatten to edges (deferred). Loose reference-by-id (no hard FK); add referential validation later.

## What's DONE + VERIFIED
1. **Option A backend (resolve-by-id):** `ISourceConnectionRuntimeResolver` (impl `FHIRBridge.Infrastructure/Sources/SourceConnectionRuntimeResolver.cs`) resolves a source node's `sourceConnectionId`→runtime `FhirSourceConfiguration` at run time. `MappingNodeExecutor` resolves `mappingProfileId`→fields/resourceType/destinationObject. Destination executor reads secret ref from node config. GET list endpoints `ConfigurationCatalogController`: `/api/v1/source-connections|/destinations|/mapping-profiles`.
2. **Catalog + save/load/run/validate:** `workflow-catalog`, `POST /workflows/validate` (validate-by-body), save/load round-trip (positions + `__transformId` preserved), `WorkflowGraphMapperService` (frontend serializer, category-as-int, merge→edges, synthetic Mapping for Source→Destination).
3. **B1 secret subsystem (Increment 1):** `ISecretWriter` → `DbSecretStore` (encrypt via DataProtection purpose `FHIRBridge.Secrets.v1`), `ProvisionedSecrets` table (migration `20260707103219_AddProvisionedSecrets`), wired into `CompositeSecretProvider` (checks DB before config/KeyVault).
4. **Increment 2 core (create-on-save secret provisioning):** `CreateDestinationConfigurationRequest.InlineSecret`; `ConfigurationService` provisions it via `ISecretWriter` on create/update, stores only the reference. **Live-verified:** create SQL dest w/ inline connstr → encrypted row (no plaintext) → run workflow referencing the secret ref **with NO config env var** → resolved from DB store → wrote row to `dbo.EpicPatientLaunch`.

5. **Increment 2 — create-on-save orchestration (DONE + live-verified):** `POST /api/v1/workflows/build` (`WorkflowEndpoints.cs`, gated `UnifiedAdmin`) takes the canvas graph **plus** create-specs tagged with the client node id they belong to (`WorkflowBuildRequest`/`SourceBuildSpec`/`DestinationBuildSpec`/`MappingBuildSpec` in `WorkflowBuildModels.cs`). It: provisions the destination inline secret + creates the destination → injects `destinationId`/`secretKeyVaultName`/`secretName`/`target` into the dest node; creates sources → injects `sourceConnectionId`; creates mappings (resolving source/dest by their node ids, or falling back to ids already on picked nodes) → injects `mappingProfileId` into the mapping node **and mirrors `resourceType`/`destinationObject`/`fields` onto the destination node** (the dest executor rebuilds its write-time mapping from its own config, so without this the writer defaults the target to the resource type). Then saves the graph and returns `{workflowId, sourceConnectionIds, destinationIds, mappingProfileIds}` keyed by node id. Also fixed: registered `JsonStringEnumConverter` on the **minimal-API** JSON options (`ConfigureHttpJsonOptions` in `Program.cs`) so build DTOs accept enum names like the MVC controllers (numeric enums still deserialize → category-as-int graph serializer unaffected). **Live-verified:** built Sample→Mapping→SqlServer graph in one call → provisioned encrypted secret (no plaintext) → ran workflow → wrote row to `dbo.EpicPatientLaunch` (6→7), identical to the Option-A path (`PatientId` populated; other fields NULL only because the *sample* Patient lacks them, same as prior sample runs).
   - **Ordering note:** creation is dependency-ordered (dest → source → mapping → workflow) but **not wrapped in one transaction** — `SqlWorkflowDefinitionStore.SaveAsync` opens its own tx and EF forbids nesting. A mid-sequence failure leaves orphaned config rows / an orphaned encrypted secret (metadata, no PHI). Acceptable for now; add delete-on-failure cleanup or a UoW seam if it matters.

Tests: 42 (Runtime.UnitTests) + 50 (UnitTests) green after all changes.

## REMAINING work
- **Increment 3 (DONE + verified): workflow launch-url + execution.** `GET /api/v1/workflows/{workflowId}/launch-url` live-verified → 302 to Epic authorize. Callback→run-workflow wired (`PendingAuthorization.WorkflowId`→`TriggerWorkflowRunAsync`), needs a real Epic launch to verify. Original design note below (implemented): `GET /api/v1/workflows/{workflowId}/launch-url` (mirror `OAuthController` `pipelines/{routeId}/launch-url` + `InteractiveSourceAuthorizationService.BuildLaunchContextToken`). Launch path: resolve workflow → its source node's `sourceConnectionId` → validate `iss` vs that source's trusted issuers → OAuth → `PendingAuthorization` carries the **workflowId** → `CompleteAsync` runs the workflow via `IRankedWorkflowOrchestrator` (instead of `TriggerRouteRunAsync`). Key files: `src/Api/FHIRBridge.Api/Controllers/V1/OAuthController.cs`, `src/FHIRBridge.Infrastructure/Sources/InteractiveSourceAuthorizationService.cs` (methods: `BuildLaunchContextToken`, `StartEhrLaunchFromContextAsync`, `IssueAuthorizationAsync`, `CompleteAsync`, `TriggerRouteRunAsync`), the launch protector `ILaunchTokenProtector` / `DataProtectionLaunchTokenProtector`, and `PendingAuthorization`.
- **Increment 2 create-on-save orchestration: DONE + verified** — see item 5 in "What's DONE" (`POST /api/v1/workflows/build`). The backend single-call option was built; the frontend can now make one call instead of 4.
- **Increment 2 remainder:** source inline **client-secret** provisioning (same pattern on `CreateSourceConnectionRequest`; Epic launch is a *public* client so low-pri); mapping `ValueType`/`IsRequired` defaults; destination `writeMode`/`schema` home (needs entity+migration, deferred). Cleaner-than-mirror follow-up: have `DestinationNodeExecutor` resolve the upstream mapping by id (like `MappingNodeExecutor`) instead of the build endpoint mirroring `fields`/`destinationObject` onto the dest node (denormalized, can go stale if the mapping is later edited).
- **Increment 4 (frontend):** wizards write the fields the create endpoints / `build` expect (destination assembles SQL connstr / `sftp://` URI as `inlineSecret`; source adds a **trusted-issuers** field + derives scopes from resources); connection **pickers** (from the list endpoints) OR create-on-save via `POST /workflows/build`; the launch-url button. Needs browser verification.

## Field-alignment gaps (frontend wizard → backend create DTO)
- **Destination:** wizard has raw `server/database/username/password` (SQL) or `host/port/username/password/folder` (SFTP) → frontend must **assemble** the connection string / `sftp://user:pass@host:port/folder` and send as `inlineSecret` (+ `keyVaultName`/`secretName` reference names). `writeMode`/`schema` have no entity home yet (`schema` is already inside `Target` as `schema.table`).
- **Source:** wizard lacks a **trusted-issuers** field (backend `SourceInteractiveConfigurationDto.TrustedIssuers` exists and is **mandatory for EHR launch**); `scopes` must be derived from `resources`+`scopeVersion`; raw `clientSecret`→provision via inline secret.
- **Mapping:** wizard row `{businessField, fhirPath, column, per-resource table}` → `MappingFieldDto{TargetField=column, JsonPath=fhirPath, ValueType(default String), IsRequired(default false)}`; inject created `SourceConnectionId`/`DestinationId`.

## How to run / verify locally
- **SQL** (docker): `fhirbridge-controlplane-sql` on `localhost,1433`, sa / `Your_password123`. DBs: `FHIRBridge` (control plane), `FHIRBridge_Output` (destination writes).
- **Start API on :5000** (from repo root):
  ```
  env "Workflow__GraphExecution__Enabled=true" "Workflow__GraphExecution__SourceConnectionIds__0=b8125b56-9018-485d-bcce-b34e38b65f2c" \
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5000 \
    dotnet run --project src/Api/FHIRBridge.Api --no-launch-profile
  ```
  (Add `"Secrets__local__warehouse-conn=Server=localhost,1433;Database=FHIRBridge_Output;User Id=sa;Password=Your_password123;TrustServerCertificate=True;Encrypt=True"` only if using a config-based secret; provisioned DB secrets need no env var.)
- **Portal:** `cd portal && npm start` → http://localhost:4200 (apiBase already `http://localhost:5000`).
- **Login:** `POST /api/v1/auth/internal/login` `{ "email":"manjyot.chaudhary@pegasusone.com","password":"Manjyot123@chaudhary" }` → Bearer token. (Manjyot was granted the `Admin` role in the dev DB earlier; password was set via the dev reset flow.)
- **Build:** stop the API first (it locks DLLs), then `dotnet build src/Api/FHIRBridge.Api/FHIRBridge.Api.csproj`. Migrations apply at **startup** (`Database.Migrate()`); the `dotnet ef database update` CLI (10.x) trips a spurious PendingModelChangesWarning — use startup instead.
- Windows: the `dotnet run`/`npm start` background shells report "exit 127" spuriously but keep serving (verify with curl). Stop a running API by killing the PID on :5000.

## Key artifacts (dev DB, may change)
- Epic source (EHR launch, trusted iss = `https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4`): `b8125b56-9018-485d-bcce-b34e38b65f2c`
- Sample source (token-free, for verification): `24fd7735-6da4-4287-a0f1-e8646d778ec4`
- Patient MappingProfile (→ `dbo.EpicPatientLaunch`): `dbc9ea94-e475-4252-94b5-4bbd69293346`
- Destinations: `3ab480ea-...` (config-secret) and `310140a9-...` (provisioned secret `workflow-secrets`/`dest-optB-test`)
- Encounter/Observation/Condition mappings also exist; routes exist for all four.
