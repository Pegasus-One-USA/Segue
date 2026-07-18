# Governance & Observability Logging — 100% Parity Implementation Plan

> ## ✅ COMPLETED 2026-07-18
> All four tiers below were implemented, verified (build/test/portal build + live-infra scratch tests against real
> SQL Server/RabbitMQ, all deleted after passing), and are now live. See `08-governance-logging-status.md` rev. 7
> for the full record, including the honest residual caveats within the 31-screen spec (Retry History/Queue Monitor
> depend on real production traffic to show non-empty data; destination Endpoint Health covers 12/21 types;
> API Analytics/System Health are in-process-only snapshots). This plan document is kept as the historical record
> of what was authorized and how it was scoped — not something to re-execute.

> **Audience:** Claude Code (or any developer) told to "implement with this context file."
> **Created:** 2026-07-18, against `docs/backend/08-governance-logging-status.md` rev. 4, branch `feature/rolebasedHealthapp`.
> **Companion doc:** `08-governance-logging-status.md` is the living status tracker — update it (new revision entry, "At a glance" table, phase sections) after each tier below completes. This file is the execution plan; that file is the record of what's actually been done.

## ⚡ Standing authorization — read this first

**When the user says "implement with that context file" (or references this file by name/path), proceed through every tier below in order, without pausing to ask for confirmation on scope, priority, or the architecture decisions already made here.** Both of the significant architecture calls in this plan (the scheduling migration in Tier 3, and the OTLP commitment in Tier 3) were already explicitly confirmed by the user via `AskUserQuestion` on 2026-07-18 — do not re-ask them, do not re-litigate them, do not offer to revert to the prior "keep polling" decision. Treat this document as pre-approved scope.

Only stop and ask if you hit a **genuine external blocker** this document couldn't anticipate — e.g., no RabbitMQ/Azure Service Bus admin credentials available in an environment for Queue Monitor, a migration that would need a destructive `git`/DB operation outside the normal EF migration flow, or a discovery that contradicts a decision recorded here (in which case, flag the contradiction explicitly rather than silently picking a side). Routine implementation judgment calls (naming, exact column layout, which existing helper to reuse) are yours to make — that's what "without waiting for consent" means here.

**Verification discipline (non-negotiable, matches every prior session in this project):**
- After each tier: `dotnet build FHIRBridge.sln` (must be 0 errors) then `dotnet test FHIRBridge.sln`. The one pre-existing, unrelated failure (`DestinationExecutionHistoryGateTests.Update_throws_when_destination_has_execution_history`) is a known baseline failure — do not try to fix it as part of this work, but do not let any *new* failure slide either.
- After each portal change: `npm run build` inside `portal/`.
- Anything touching EF Core bulk operations (`ExecuteDeleteAsync`, etc.) must be verified against the real SQL Server in `docker-compose.yml` (`fhirbridge-controlplane-sql`), not just EF InMemory — InMemory silently doesn't support several EF Core 9 bulk APIs. Use a temporary scratch test, confirm it passes, then delete the scratch file (it depends on live infra and doesn't belong in the permanent suite) — same pattern used for the Tier-1 retention work.
- Update `08-governance-logging-status.md` at the end of each tier (new revision entry + refreshed "At a glance" table/phase sections), the same way rev. 2 → rev. 4 were done.

---

## Tier 1 — cheap, no dependencies

### 1.1 Authorization / permission-denial logging (spec #11)
**Current state:** `IGovernanceLogger` has no method for this. RBAC (`HasPermission:<code>` policies) rejects requests via the normal ASP.NET Core authorization pipeline, but a denial is never written anywhere.
**Build:**
- Add `Task LogAuthorizationAsync(AuthorizationEntry entry, CancellationToken cancellationToken = default)` to `IGovernanceLogger` (`src/BuildingBlocks/FHIRBridge.Governance/`), with an `AuthorizationEntry` record (fields: `UserId`/`UserEmail`, `Action` or `RequestPath`, `PermissionCode`, `Result` ("Denied"/"Granted" — granted is optional/lower priority, denial is the compliance-relevant case), `CorrelationId`).
- New append-only table `AuthorizationLogs` (mirror the shape of `AuthenticationLogs`), migration, `Ef`/`Null` implementations.
- Hook point: a custom `IAuthorizationMiddlewareResultHandler` (ASP.NET Core's extension point for intercepting authorization failures) registered in `Program.cs`, so this is a single hook, not scattered per-controller calls — consistent with the "registry over switch" convention and the zero-touch pattern `AuditingSaveChangesInterceptor` already uses for audit logging.
- New `GET /api/v1/governance/authorization-logs` endpoint on `GovernanceController`, correlation-id filterable.
- New portal screen `governance/authorization-logs` (copy the shape of `authentication-logs`), added to sidebar + `app.routes.ts`.
**Acceptance:** a request to a `[Authorize(Policy = "HasPermission:configuration.write")]` endpoint by a user lacking that permission produces one `AuthorizationLogs` row, visible in the new screen and in Correlation Search.

### 1.2 Retention Policies screen (spec #22)
**Current state:** fully enforced already — `GovernanceLogPurgeableStore<T>` (9 tables), `RetentionPurgeService` (per-store policy via `ConfiguredRetentionPolicyService`, config section `Governance:Retention`), `RetentionPurgeWorker` hosted. No UI.
**Build:**
- Read endpoint `GET /api/v1/governance/retention-policies` returning, for every registered `IPurgeableStore` plus the 4 immutable tables, its `DataClass`, resolved `RetentionYears`, and whether it's immutable/purgeable — reuse `IRetentionPolicyService.GetPolicy(dataClass)` per store, and enumerate registered `IPurgeableStore` instances via DI (`IEnumerable<IPurgeableStore>`).
- Portal screen `governance/retention-policies` — a simple read-only table (Log Type / Retention / Purgeable Y-N), matching the spec's mockup shape. Editing (if desired) would mean writing back to `Governance:Retention` config — out of scope unless the user asks; ship read-only first.
**Acceptance:** the screen lists all 13 governance/operations tables with their real, currently-effective retention years — not a hardcoded mockup.

### 1.3 Archive (spec #30)
**Current state:** `RetentionPurgeService`/`GovernanceLogPurgeableStore<T>.PurgeOlderThanAsync` does a hard `ExecuteDeleteAsync` — no archive step.
**Build:**
- Before the bulk delete, serialize the expiring rows to a blob (reuse whatever blob storage abstraction the destination writers already use for file-based destinations — check `src/FHIRBridge.Infrastructure/Destinations/` for an existing `IBlobStorageClient`/similar before adding a new one) as a dated JSON/NDJSON archive file, one per `(DataClass, purge run)`.
- New table or JSON manifest tracking `ArchivedThrough` date per `DataClass` + the blob location, so the portal can show "archived through" and a restore action.
- `GET /api/v1/governance/archives` + a `POST /api/v1/governance/archives/{dataClass}/restore` (re-inserts archived rows for a given date range — lower priority, can be a stub returning `501 Not Implemented` with a clear message if genuinely out of scope for this pass; **do not fake success**).
- Portal screen `governance/archive`.
**Acceptance:** a purge run produces a retrievable archive artifact before deleting; the screen shows `ArchivedThrough` per log type truthfully.

### 1.4 Log Settings (spec #29)
**Current state:** PHI masking is hard-coded on (`PhiMaskingEnricher`, no toggle — and it should probably **stay** non-negotiable for PHI masking specifically; don't add a toggle that lets someone turn off PHI masking). Payload logging isn't a configurable per-tenant opt-in. No screen showing which event categories write to `AuditLog` vs. Serilog-only.
**Build:**
- A read-only settings screen is likely the right scope here, not a "toggle everything" screen — **do not add a UI toggle for disabling PHI masking**, since that would itself be a compliance regression. Scope this to: (a) which categories are currently enabled/routed where (read from existing config/DI registrations), (b) the payload-logging opt-in flag if/when one exists (currently doesn't — flag this explicitly as "not implemented" rather than building a fake toggle with no backing behavior).
- If genuinely building a per-tenant payload-logging opt-in: new `Tenant`-scoped config flag, auto-purge tied to the same retention mechanism as 1.3.
**Acceptance:** the screen accurately reflects real, currently-enforced settings — no toggle that doesn't actually change backend behavior.

### 1.5 OAuth as its own screen (spec #13)
**Current state:** logged into `AuthenticationLogs` with `AuthenticationType = "OAuth:{grantType}"`, viewable only by scrolling the shared Authentication Logs screen.
**Build:** portal screen `governance/oauth-logs` — same `GET /api/v1/governance/authentication-logs` endpoint, client-side filtered/pre-filtered server-side via a query param (`?authenticationType=OAuth`) to `AuthenticationLogController`/`GovernanceController`.
**Acceptance:** dedicated screen showing only OAuth token issuance/refresh events, per your original mockup #13's columns (Time/Event/Client/Result).

### 1.6 Version History / Configuration Comparison polish (spec #18/#19)
**Current state:** "History" and "View diff" actions on the Audit Logs screen reuse raw `AuditLog` rows — functionally equivalent but not visually a labeled `v12`/`v13` sequence or a dedicated two-column comparison page.
**Build:**
- Compute a per-entity version number client-side (or via the `entityType`/`entityId`-filtered endpoint, ordinal position ascending) — no new table needed, this is a rendering change.
- A dedicated `governance/configuration-comparison` page: pick two versions from the entity's history, render as an explicit two-column diff (reuse the existing client-side JSON-diff logic, just change the layout from inline-per-row to side-by-side-by-picker).
**Acceptance:** entity history shows `v1, v2, v3...` labels; comparison page lets picking any two versions side-by-side, matching the mockup layout.

---

## Tier 2 — new screens against endpoints that already exist

### 2.1 Workflow Executions list (spec #1) + Resource Processing (spec #3)
**Current state — important finding from the 2026-07-18 audit:** the backend already has almost everything needed, for the **Configured Pipeline plane** (the plane your spec's examples actually describe — `Scheduler:Route-2291`-triggered runs), but **no portal screen consumes it**:
- `GET /api/v1/pipeline-runs/route-executions` (`PipelineRunsController.GetRouteExecutions`) — status/source/triggeredBy/search-filterable, paged, returns `PipelineRunRouteExecutionDto` (`PipelineName`, `SourceName`, `Status`, `StartedOnUtc`, `CompletedOnUtc`, `ExtractedCount`, `MappedCount`, `WrittenCount`, `ErrorMessage`, derived `DurationMs`).
- `GET /api/v1/pipeline-runs/route-executions/{id}/resources` (`PipelineRunsController.GetRouteExecutionResources`) — per-resource-type drill-down (`PipelineRunResourceHistoryDto`), decrypted PHI payloads, paged.
- The existing `portal/src/app/execution-history/` screen is wired to a **different, unrelated system** — the Runtime DAG plane's `WorkflowRunHistoryDto`/`workflow-runs` endpoints. Do not confuse the two or try to reuse that screen's component as-is; build new ones.
**Gap to close first:** `PipelineRunRouteExecutionDto` has no `CorrelationId` field. Add it — join back to the parent `ConfiguredPipelineRunRecord.CorrelationId` via `PipelineRunId` (either a SQL join in the repository or a denormalized column copied at write time; prefer the join to avoid drift).
**Build:**
- New portal module `portal/src/app/pipeline-executions/` (or similar — avoid colliding with the existing `execution-history` naming) with a list screen (spec #1's columns: RunId, Workflow/Pipeline, Started, Duration, Status, Triggered By, Read/Exported/Errors counts — `ExtractedCount`/`WrittenCount` map directly, "Errors" needs a count derived from `ErrorLogs` filtered by `CorrelationId` or `RouteExecutionId`) and a detail screen showing the per-resource-type breakdown (spec #3's ResourceType/ResourceId/Stage/Status/ProcessingTime/Note columns — map from `PipelineRunResourceHistoryDto`).
- Add to sidebar (new "Pipeline Executions" or fold under existing "Operations" section — your call at implementation time) and `app.routes.ts`.
**Acceptance:** a real Configured Pipeline run (Worker-triggered or manually via `POST /api/v1/pipeline-runs`) is visible end-to-end: list row → detail → per-resource rows, all correlation-searchable.

### 2.2 Execution Timeline (spec #2)
**Current state:** Correlation Search (`governance/correlation-search`) already returns every category's rows for one `CorrelationId`, but as separate sections, not one merged chronological view.
**Build:** a "Timeline" rendering mode on the Correlation Search results (or a new tab on the pipeline-execution detail screen from 2.1) that flattens all returned rows — `SchedulerHistory`, `AuthenticationLogs` (auth step), `DataAccessLogs` (extraction/governance), `ExportHistory`, `RetryHistory`, `ErrorLogs`, `NotificationHistory` — into one time-ordered list, each entry showing its category as a step type (mirroring your mockup's `09:00:00 Workflow Started`, `09:01:10 Governance: Patient/77213 DENIED`, etc.), each expandable to the raw record. This is pure client-side composition over data `GetCorrelationSearchResultAsync` already returns — no new backend endpoint needed.
**Acceptance:** pasting a `CorrelationId` shows one interleaved, chronological, step-typed timeline matching the mockup's shape.

### 2.3 Endpoint Health for destinations (spec #9, completing existing partial)
**Current state:** `EndpointHealthCheckWorker`/`EndpointHealthChecks` covers source connections only, via `ISourceConnectionTestService`.
**Build:** a generic `IDestinationHealthCheckProvider` per destination type (registry/strategy — 21 types, no switch statement, matching the existing `FhirSourceConnectorBase` / `*ApplicationStrategy` conventions). Start with the destination types that already have a "test before save" flow in the config wizard (reuse that logic where possible) rather than writing 21 bespoke checks from scratch. Extend `EndpointHealthCheckWorker` to also iterate destinations.
**Acceptance:** the Endpoint Health screen shows both source and destination rows (e.g., your mockup's "Partner SFTP — Warning — 640ms").

### 2.4 Compliance Reports: separate SOC2 export (spec #21, completing existing partial)
**Current state:** one combined HIPAA/SOC2 PDF exists (`GET /api/v1/governance/reports/hipaa-audit`).
**Build:** a second report type/endpoint (`GET /api/v1/governance/reports/soc2-evidence`) with its own QuestPDF template emphasizing SOC2-relevant framing (access reviews, change management evidence from `AuditLogs`, availability/incident evidence from `SecurityEvents`/`EndpointHealthChecks`) — reuse `IAuditChainVerificationService` and the existing report-generation scaffolding, don't duplicate the hash-chain-walk logic. A "scheduled monthly" generation (per your mockup) would need a new scheduled worker — decide at implementation time whether that's in scope for this pass or a manual-trigger-only report for now (manual-trigger-only is an acceptable, honestly-labeled reduction in scope; don't fake a "Scheduled" status in the UI if nothing schedules it).
**Acceptance:** two distinct report types are downloadable and listed separately on the Compliance Reports screen.

---

## Tier 3 — the two confirmed architecture projects

### 3.1 Scheduling migration: retire Worker.cs polling, make the queue path live
**Decision (already confirmed 2026-07-18, do not re-ask):** migrate scheduling fully to `ScheduleDispatcher`/`ScheduleDispatcherWorker`/`PipelineRunCommandProcessor`, retiring `Worker.cs`'s direct-call polling. This reverses the earlier "keep polling, mark queue path dead" decision — that reversal is intentional and was made specifically to unlock Retry History (#5) and Queue Monitor (#4), which are structurally incapable of showing real data under polling.
**Build, in order:**
1. Confirm `IScheduleEvaluationService.ClaimDueRunsAsync`'s atomic claim is actually safe under concurrent Worker instances (re-verify, don't assume — this was the original reason polling was kept).
2. Register `ScheduleDispatcherWorker` and `PipelineRunCommandProcessor` in `src/Worker/FHIRBridge.Worker/Program.cs`.
3. Remove `Worker.cs`'s direct scheduling call (`RunDueRoutesAsync`/`RunDueWorkflowsAsync` calling `IConfiguredPipelineService.StartAsync` directly) — or gate it behind a feature flag defaulting to off, so a fast rollback is possible if the queue path misbehaves in practice. Prefer the feature-flag approach given this is a production-behavior change.
4. **Fix the webhook silent-message-loss bug as part of this work**, since a live consumer becomes required anyway once you're touching this area: register `WebhookIngestionCommandProcessor` too, so `WebhookIngestionController`'s `IWebhookIngestionDispatcher.EnqueueAsync` calls actually get consumed.
5. Remove or repurpose the rev.-4 `<remarks>` "NOT CURRENTLY REGISTERED/HOSTED" doc warnings on these classes — they're now stale once this migration ships.
6. Re-run/extend the existing scheduler tests to cover the queue path under concurrent dispatch (this is the risk the original "keep polling" decision was protecting against — prove it's actually safe, don't just assume the atomic claim works).
**Acceptance:** a due route is claimed and run exactly once even with multiple Worker instances running concurrently (test this explicitly, not just single-instance); webhook ingestion no longer silently drops messages.

### 3.2 Retry History goes live (spec #5)
**Build:** none — `RetryHistory` logging already exists in `PipelineRunCommandHandler`/`WebhookIngestionCommandHandler` via `MessageRetry.ExecuteAsync`. This item is "done" automatically once 3.1 ships; just verify a real retry (e.g., inject a transient destination failure in a test/dev run) produces a row and is visible in `operations/retry-history` and Correlation Search.

### 3.3 Queue Monitor (spec #4)
**Build:**
- `IQueueMonitorProvider` abstraction (registry/strategy, one implementation per `Messaging:Provider` value — `InMemory` can return a trivial/not-applicable result, `RabbitMQ` calls its management HTTP API for queue depth/pending/dead-letter counts, `AzureServiceBus` calls its admin client).
- `GET /api/v1/operations/queue-monitor` + portal screen `operations/queue-monitor` matching your mockup's columns (Queue/Type/Pending/Processing/Dead-Letter/Avg Wait/Last Message).
- **If admin API credentials aren't available in a given environment**, the screen must say so explicitly ("Queue monitoring unavailable — no management API access configured") rather than show empty/fake rows. This is the one place in this plan where a real external blocker (missing credentials) is expected and should stop you from faking success — surface it, don't paper over it.
**Acceptance:** queue depth/dead-letter counts are real and change when messages are enqueued/consumed/dead-lettered, verified against whatever transport is actually configured in the dev docker-compose environment (RabbitMQ).

### 3.4 OpenTelemetry metrics wiring (Phase 10) → API Analytics (#8) + System Health (#27)
**Decision (already confirmed 2026-07-18, do not re-ask):** build real OTel metrics export rather than hand-rolled SQL aggregation.
**Build:**
- Extend `FHIRBridge.Observability` with metrics instrumentation: request counters/histograms for API calls (top APIs, p95/p99 latency, error rate, retry rate — sourced from the same `ApiRequestLoggingHandler` hook point already used for `ApiRequestLogs`, but emitted as OTel metrics instead of/alongside DB rows), and process-level resource metrics (CPU/memory) for the Api/Worker hosts.
- Export to the OTEL collector already in `docker-compose.yml` (matches the "Azure Monitor in cloud" note in `CLAUDE.md`'s Observability section).
- `GET /api/v1/operations/api-analytics` and `GET /api/v1/operations/system-health` — these can either proxy/query the OTel backend if it exposes a query API, or (more likely, simpler) maintain lightweight in-process aggregation fed by the same metrics instruments, exposed via a normal endpoint. Decide based on what's actually feasible without adding a new query-capable metrics backend as a hard dependency — don't block this screen on standing up Prometheus/Grafana if that's not already planned infrastructure.
- Portal screens `operations/api-analytics` and `operations/system-health` matching your mockups (Top APIs, Slowest, Error rate, Retry rate / Component-CPU-Memory-Notes table).
**Acceptance:** both screens show real, currently-accurate numbers, sourced from OTel instrumentation, not static or SQL-percentile-approximated data.

### 3.5 Scheduler summary screen (spec #25)
**Current state:** `operations/scheduler-history` is a real dispatch-event log; `schedules` route is a "Coming Soon" stub.
**Build:** replace the `schedules` stub with a real per-route summary — `Route`, `Next Run` (computed from the route's cron/schedule expression + `ScheduleExpressionMatcher`), `Last Run` + `Duration` + `Status` (most recent `SchedulerHistory` row for that route, now meaningfully populated via the queue path from 3.1). Query `ResourcePipelineRoute` joined to its latest `SchedulerHistory` entry.
**Acceptance:** the screen shows every configured route with accurate next/last run info, matching your mockup exactly.

---

## Tier 4 — deliberate, security-reviewed builds

### 4.1 Data Lineage (spec #20)
**Build, with a specific safety design (already decided, do not re-ask/re-scope down further without flagging it):**
- Build the full lineage tree structure (source field → mapping rule/profile version → destination column → export file/row) — this part is PHI-free (it's metadata about the mapping, not values) and can be shown to anyone with `governance.read`.
- Gate the **actual field value** behind a stricter check: a new permission (e.g. `governance.revealPhi` or similar — follow the existing `PermissionCode`/`[StandardPermission]` reflection-based registration pattern) and, critically, **viewing a real value must itself write a `DataAccessLog` entry** (this is the "own audited PHI access" pattern already used elsewhere in this codebase) — do not add a silent decrypt-and-display path.
- Backend: a query over `PipelineRunResourceRecord.FetchedJson`/`NormalizedJson`/`MappedValuesJson` (already `IPhiFieldEncryptor`-encrypted at rest) joined with the `MappingProfile` used for that run's version, to reconstruct the tree; decrypt only on the gated reveal action.
- Portal screen `governance/data-lineage` rendering the tree (structure always visible, values behind a "Reveal" button gated by the new permission).
**Acceptance:** the tree structure is visible to any governance-read user; actual field values require the stricter permission and produce their own audit trail entry per view.

### 4.2 Alert Engine + Alert Rules (spec #26/#31)
**Build:**
- New tables: `AlertRule` (condition expression, severity, channel, recipients — e.g. `count(failed logins, 15min) > 3`), `AlertHistory` (fired/acknowledged).
- A rule-evaluation service polling/subscribing to the relevant governance tables (`SecurityEvents`, `EndpointHealthChecks`, etc.) on an interval, evaluating each active `AlertRule`, writing `AlertHistory` on fire, dispatching via the existing `NotificationHistory`/Email channel (and PagerDuty/Teams only if those integrations already exist elsewhere in the codebase — check before assuming they need to be built from scratch; if they don't exist, scope those channels out explicitly rather than stubbing fake delivery).
- `GET/POST /api/v1/governance/alert-rules`, `GET /api/v1/governance/alerts` (history) — portal screens `governance/alert-rules` (CRUD) and `governance/alerts` (history, matching your mockup's Time/Rule/Severity/Fired/Acknowledged columns).
**Acceptance:** a real condition (e.g., 3+ failed logins in 15 minutes, already tracked by `SecurityEvents`) fires a real alert, visible in both screens.

---

## Final step, every time all tiers are complete

Update `docs/backend/08-governance-logging-status.md` to the next revision: refresh the "At a glance" table (should read at or near 100% across every phase, with any remaining honest caveats called out explicitly — e.g., Queue Monitor's per-environment credential dependency, Data Lineage's gated-reveal design, SOC2 report's manual-trigger-only status if that's what shipped), and add a revision-log entry summarizing every tier above in the same style as rev. 2 → rev. 4.
