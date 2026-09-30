# Code review record — External Workflow Trigger / API Clients

Branch `feature/Slient-Secret-Module` (base `cb7c3062`). **Nothing in this work is committed yet** — everything below is
uncommitted working-tree state. Written so the review can be resumed later without re-reading the whole conversation.

Status legend: **FIXED** (changed + verified) · **ACCEPTED** (deliberate, documented) · **UNRELATED** (pre-existing, not ours) ·
**OPEN** (still to check).

---

## 1. What was built (inventory)

| Area | What | Main files |
|---|---|---|
| API Clients (OAuth2 client credentials) | Tenant-wide Client ID/Secret, `POST /api/v1/oauth/token`, Bearer token triggers `POST /workflows/{id}/run` | `ApiClient.cs`, `ApiClientService.cs`, `ClientCredentialsTokenService.cs`, `ClientCredentialsAccessTokenIssuer.cs`, `ApiClientsController.cs`, `ClientCredentialsTokenController.cs` |
| Return URLs | Per-client allow-list; each entry is **Exact** (full URL) or **Domain** (origin only) | `ApiClientReturnUrl.cs` (`ReturnUrlMatchMode`), `ApiClientService.cs` |
| Browser-redirect trigger | `POST /workflows/external/run` (validates, runs, 302s to a registered return URL) | `WorkflowEndpoints.cs`, `ExternalWorkflowTriggerService.cs` |
| Hosted run page | `POST /workflows/external/run-page` validates then redirects to portal `/external-run`; portal drives non-interactive polling **or** the interactive (patient/provider consent) flow | `WorkflowEndpoints.cs`, `portal/src/app/external-run/` |
| Helper endpoints | `/workflows/external/list` (adds `sourceSystemType`), `/external/ehr-endpoints` (`code` = EhrEndpoint **Id**), `/external/run-status/{runId}`, `/external/return` | `WorkflowEndpoints.cs` |
| Window behaviour | `window_mode` (`new`/`same`), `close_on_complete`; return via **signed, expiring token** (Data Protection, 2h) — never a raw URL in the query | `WorkflowEndpoints.cs`, `external-run.component.ts` |
| Portal UI | API Clients list; Return URLs **table screen** (URL / Full Domain / Edit / Delete) + add/edit dialog | `portal/src/app/api-clients/` |
| Demo app | `external-trigger-console` (creds, workflow + EHR-endpoint dropdowns, remember-creds, window options), `client-credentials-console` | `Demo_TestApp/` |
| Persistence | Migrations `AddApiClients`, `AddApiClientReturnUrls`, `AddApiClientReturnUrlMatchMode` — **both** SQL Server (`src/FHIRBridge.Infrastructure`) and PostgreSQL (`...Migrations.PostgreSql`) | see `git status` |
| Dev proxy | `portal/proxy.conf.json` + `angular.json` `proxyConfig`: `localhost:4200/api/*` → `localhost:5000` | needs `ng serve` restart |

Shared-code touch points (regression surface): `WorkflowExecutionContext` (new optional trailing `ehrEndpointCode`),
`/run` handler (client-credential vs portal-user branching), `FHIRBridgeDbContext` (DbSets), `Application`/`Infrastructure`
`DependencyInjection.cs`. The Worker's own code is **unchanged**.

---

## 2. Review method

1. `/code-review` (read-only diff review, no build/run) → 6 findings.
2. Each finding re-verified against the code by hand before acting.
3. Fixes built, unit-tested, and exercised against a live scratch API on a spare port (5099) so the user's running stack
   (5000/5500/5501/4200) was not disturbed.

---

## 3. Findings and disposition

| # | Finding | Verdict | Where |
|---|---|---|---|
| 1 | Client-credentials JWTs are stateless: disabling/rotating a client doesn't revoke tokens already issued (15 min default) | **ACCEPTED** — intentional; documented in the Edit dialog ("tokens already issued expire naturally") | `ClientCredentialsAccessTokenIssuer.cs` |
| 2 | `ReadExternalTriggerFieldsAsync` only caught `JsonException` → missing Content-Type / bad multipart gave **500** | **FIXED** — now catches `JsonException`, `InvalidOperationException`, `InvalidDataException`, `IOException`, `BadHttpRequestException` → clean 400. Verified: text/plain, no content-type, bad JSON, bad multipart all 400; valid form still 200 | `WorkflowEndpoints.cs` |
| 3 | `/external/run` sync branch had `try/finally` with no `catch` → thrown run = bare 500 | **FIXED** — redirects to the validated return URL with `status=Failed` | `WorkflowEndpoints.cs` |
| 4 | `AppendExternalTriggerQuery` appended after a `#fragment` (`…#/done?status=…`) | **FIXED** — params inserted before `#`. Verified `…/?status=Succeeded#/done` | `WorkflowEndpoints.cs` |
| 5 | Anonymous `run-status` returned raw `run.ErrorMessage` (real leak seen: `Epic token endpoint returned 400 … invalid_client`) | **FIXED** — generic message + `ErrorReferenceId` (portal's existing PHI-safe convention). Verified old vs new build on same run | `WorkflowEndpoints.cs` |
| 6 | Exact-mode matching compared raw caller string to normalized stored URL (case / `:443` / encoding rejected) | **FIXED** — both normalized via `GetLeftPart(Path)`; exact entries still refuse query/fragment. 6 new unit tests | `ExternalWorkflowTriggerService.cs` |

Other bugs found and fixed during the work (not from the review skill):
- **`NgForm` swallowed the demo form's native POST** — component imports `FormsModule`, which auto-attaches `NgForm`; its
  submit handler returns `false` (preventDefault). Symptom: enabled button, no request, no error, also in incognito.
  Fix: `ngNoForm` on the `<form>`.
- **EF: new child entity tracked as `Modified` not `Added`** → `DbUpdateConcurrencyException` (UPDATE of a non-existent row).
  Fix: `builder.Property(x => x.Id).ValueGeneratedNever()` on `ApiClientReturnUrl`. Same trap applies to any child entity
  with a client-set Guid key added only via a parent's navigation.
- Portal read `err.error?.title`; API returns `{ error, message, … }` → real messages were masked. Use
  `err.error?.error ?? err.error?.title ?? err.error?.message`.
- `EhrEndpointCode` semantic: must be the EhrEndpoint **Id** (Guid), not `VendorEndpointId`.
- Domain-mode validation now **rejects** a path/query/fragment instead of silently stripping it.
- Unit test call site broke when `ValidateCredentialAsync` gained a `refererHeader` parameter (caught by a full-solution build).

---

## 4. Verification evidence

- API compiles: 0 errors; the 8 warnings are all pre-existing files.
- `FHIRBridge.UnitTests`: **2051 / 2053 pass**. The 2 failures are **UNRELATED**:
  - `WarehouseTableLandingStrategyTests.The_only_DDL_is…` — walks up from the test DLL to find `FHIRBridge.sln`; fails only when run from a
    scratch `-o` folder outside the repo.
  - `DateTimeFormatBirthDateReproTests…` — culture-dependent (`18-08-1993 12.00.00 AM` under this machine's locale).
- `FHIRBridge.ArchitectureTests`: **8 / 8 pass** (run in place) — including both CSRF-exemption rules covering the new endpoints.
- `FHIRBridge.Runtime.UnitTests`: **366 / 367**. **UNRELATED**: `SourceNodeExecutorSyncCursorTests.Each_resource_type_carries_its_own_lastUpdated_watermark` — null `page`
  at `SourceNodeExecutors.cs:787` (fake connector returns nothing); file untouched by this work, test dates from August.
- `FHIRBridge.Api.IntegrationTests`: **pre-existing harness failure** (`Database:Provider: PostgreSql` leaks into the "Testing" environment) — not re-run.
- Real-browser (Playwright, run as a script) checks passed: demo console → new-window+auto-close (popup closed itself), new-window+keep-open (popup ended on
  return URL), same-window (returned to console with result panel); interactive flow reaches athenahealth's real login page.

---

## 5. OPEN items — pick up here

1. **Restart the API** on 5000 (the running one is the pre-review build). Use the restart script (see §6). Portal/demo hot-reload.
2. **Interactive return leg is untested end to end.** Outbound leg proven (portal → token-status → mint → athenahealth login). Still to verify with a real
   patient login: callback → `…/external-run?signedIn=1` → run with saved `sessionId` → result. Preconditions:
   - `http://localhost:5000/api/v1/oauth/callback` (the source connection's saved redirect URI) must be in the **athenahealth app's approved redirect URIs**
     (athena returned "not an approved redirect location" otherwise). We briefly changed the two Athena Patient connections to `:4200` and reverted to `:5000` at the user's request.
   - Workflow must have public launch enabled; EhrEndpoint must be MyChart-type for the patient flow.
3. **Scheduled workflows are not running — cause found, not yet changed.** `SystemSettings` `RuntimeWorker:Enabled = False` (seeded 2026-09-03, never modified). The
   Worker skips everything when false. **Not caused by this work.** Enabling it will also fire `Epic_Bulk` (cron `0 2 * * *`, never run) because the catch-up window is
   24 h — decide whether to disable `Epic_Bulk` first. Target workflow: `Athena_SQL_Backend_scheduled` (Poll, 5 min).
4. **DemoApp3's secret was regenerated** by someone after 18:52 (audit log: "Invalid client id or secret") — use the current secret; the test notes here are stale.
5. **`OAuth:PublicBaseUrl = http://localhost:4200`** (System Settings, deliberate) vs the Athena source's redirect URI `:5000` — consent-entry link uses 4200 (needs the dev proxy),
   the OAuth callback uses the source's own `:5000`. Decide whether to keep them different.
6. **Documentation rewrite paused.** `Documents/Client-Credentials-Workflow-Trigger.html` §06 predates most of the above (run-page, window options, Return URL modes,
   interactive flow, signed return token, dev proxy). Planned: rewrite §06, add Return URLs / hosted run page / helper-endpoint reference / security & troubleshooting sections.
7. **Review gaps not yet examined:** the three SQL Server migrations were generated but not confirmed against a SQL Server database (only PostgreSQL is in local use);
   `Demo_TestApp/backend` proxies forward no `Referer` (so the list endpoints' Referer check is only meaningful for direct browser calls); no integration tests
   cover `run-page` / `return` / `run-status`; the portal `external-run` page has no unit test.
8. **Housekeeping:** nothing committed; `.claude_old/` and `Demo_TestApp/docs/` are untracked and not from this work; no `Co-Authored-By` trailer on commits (repo rule).

---

## 6. How to resume

- Services / ports: API `5000`, Worker (background), Gateway `5050`, Portal `4200`, Demo API `5500`, Demo App `5501`, LicenseServer `5220`.
- Restart everything (points at this checkout): `D:\Project\FHIRBridge\Docs\Run_Powershell_script\restart-local-apps-Folder2.ps1`
  (local Postgres `localhost:5432`, DB `FHIRBridge`; credentials live in that script — not repeated here).
- Full solution build needs the running processes stopped (they lock `bin\Debug\net9.0`). Without stopping them, compile into a scratch folder:
  `dotnet build src/Api/FHIRBridge.Api/FHIRBridge.Api.csproj -o <scratch>`; tests: `dotnet test tests/FHIRBridge.UnitTests -o <scratch>` (architecture tests must run in place).
- Portal typecheck: `cd portal && npx tsc --noEmit -p tsconfig.app.json`. Demo: same in `Demo_TestApp/frontend`.
- EF migrations for **PostgreSQL**: `FHIRBRIDGE_DB=<conn> dotnet ef migrations add <Name> --project src/FHIRBridge.Infrastructure.Migrations.PostgreSql`
  (no `--startup-project`; passing the Api picks the SQL Server design-time factory). For **SQL Server**:
  `dotnet ef migrations add <Name> --project src/FHIRBridge.Infrastructure --startup-project src/Api/FHIRBridge.Api`. Never `ef migrations remove` after a duplicate — delete the files.
- Browser testing without an MCP: a Playwright script run from any scratch folder outside the repo (the one used lived in Claude's temp scratchpad and is not kept) —
  `npm i playwright && npx playwright install chromium`, then drive `http://localhost:5501/external-trigger-console`.
- Throwaway DB edits made during the work (all reverted or intentional): Athena redirect URIs (reverted to `:5000`), `demoapp2` secret rotated several times,
  `http://localhost:5501` Domain entry on a client, test return-URL rows removed.
