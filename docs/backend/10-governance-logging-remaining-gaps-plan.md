# Governance & Observability Logging — Closing the Remaining 6 Gaps

> **Audience:** Claude Code (or any developer) told to "implement with this context file."
> **Created:** 2026-07-18, against `08-governance-logging-status.md` rev. 7 and `09-governance-logging-100-percent-plan.md` (completed).
> **Context:** After Tiers 1-4 of the 100% parity plan shipped (commit `3da448e1`, branch `feature/governancelogging`), the user asked whether the original 31-screen spec table could be called "100% complete." A code-level audit found six specific details in that table that the shipped implementation deliberately does not back:
>
> 1. Archive → Restore (returns 501 today)
> 2. Exports → "Downloaded By" (no download tracking exists)
> 3. Data Lineage → row-level export position ("row 812")
> 4. Compliance Reports → SOC2 "Scheduled monthly" (generation is on-demand only)
> 5. System Health → a distinct Worker row alongside Api/SQL Server (each host only reports itself)
> 6. Log Settings → functional toggles (screen is deliberately read-only)
>
> This document plans closing five of those six. **Item 6a (PHI-masking toggle) is explicitly out of scope — see the callout below.**

---

## Standing authorization — read before starting

Per the same working agreement as `09-...-plan.md`: when told to "implement with this context file," proceed through every item below in order without pausing to ask about scope or priority. The one exception is called out explicitly in Item 6 (payload-logging opt-in), which requires a real go/no-go decision before any code is written — flag it and wait rather than assuming a default.

Verification discipline is unchanged from the prior plan: for anything touching real DB writes/reads, real blob/file storage, or new background workers, write a temporary `_Scratch*.cs` test under `tests/FHIRBridge.UnitTests/...`, run it against real infrastructure (SQL Server on `localhost,1433`, the project's own MinIO/blob container, etc.), confirm it passes, then delete it. Never fake data to make a screen look populated — an honestly-empty result with a clear reason (as already done for Retry History/Queue Monitor) is correct; a fabricated row is not.

After each item completes, update `08-governance-logging-status.md` with a new revision entry (same pattern as revs. 1-7).

---

## Item 1 — Archive Restore

**Current state:** `GovernanceController.RestoreArchive` returns `501 Not Implemented`. The archive files themselves are real (NDJSON, one per purge run, written by `EfGovernanceLogArchiveWriter`), and `ArchiveManifestEntry` already records `DataClass`, cutoff date, and `FileLocation` for each one.

**Plan:**
- Add `IGovernanceLogArchiveReader.RestoreAsync(Guid manifestEntryId, CancellationToken ct)` to `FHIRBridge.Application/Abstractions/Governance/`.
- `EfGovernanceLogArchiveReader` (Infrastructure): look up the `ArchiveManifestEntry`, stream the NDJSON file back from its `FileLocation`, deserialize each line to the concrete entity type. Need a `DataClass → Type` registry — mirror the existing pattern already used by `GovernanceLogPurgeableStore<T>` registrations in `DependencyInjection.cs` rather than inventing a new one.
- Insert rows back via `DbContext.Set<T>().AddRange(...)` + `SaveChangesAsync`. Since purge only ever ran `ExecuteDeleteAsync` on rows past the cutoff, there's no PK collision risk — the IDs are gone from the live table.
- Append-only tables (`AuditLog`, `DataAccessLog`, etc.) already throw on update/delete via `AuditingSaveChangesInterceptor` — confirm inserts are unaffected (they should be; the guard only targets `EntityState.Modified/Deleted`).
- Wire `GovernanceController.RestoreArchive` to call it for real; change the route's `[ProducesResponseType]` from 501 to 200 + a `RestoreResultDto(int RowsRestored)`.
- Portal: `archive.component.ts` already has a "Restore range" action stubbed against the 501 — point it at the real response and show the row count.

**Verification:** scratch test — archive a batch of test rows, purge them, restore, assert row count and content match pre-purge state, against real SQL Server. Delete scratch file after passing.

**Effort:** Moderate. One new interface/implementation, one registry reuse, one controller change, one portal wire-up.

---

## Item 2 — Exports "Downloaded By"

**Current state:** `ExportHistoryDto` has no download-tracking fields. Exports to push destinations (SFTP, email) are correctly shown with no download action — there's nothing to click.

**Plan:**
- Scope to blob/file-backed destination types only (BlobStorage, S3, Ndjson, Parquet, Csv, Excel, Avro, Protobuf — the ones with a retrievable file). SFTP/email/webhook stay push-only with no download action, matching today's honest depiction.
- Add `DownloadedBy string?` / `DownloadedAtUtc DateTime?` to `ExportHistory` entity + EF configuration + migration `AddExportDownloadTracking`.
- New endpoint `GET /api/v1/governance/exports/{exportId}/download`: verifies the destination type is file-backed, streams the file from storage, and on successful stream start, updates `DownloadedBy`/`DownloadedAtUtc` on that row (first download wins, or overwrite each time — decide based on whether "last downloaded" or "first downloaded" is more useful; recommend last-downloaded since it answers "did anyone get this file").
- Log a `DataAccessEntry` via `IGovernanceLogger` on each download, same pattern as `DataLineageController`'s reveal-audit.
- Portal: `exports.component.ts` gets a "Download" button for file-backed rows only; disabled/absent for push-only rows.

**Verification:** scratch test hitting the real blob/file store used in Docker Compose, confirm the row updates and the audit log entry appears.

**Effort:** Moderate. Touches one entity + migration, one new endpoint, one portal button.

---

## Item 3 — Data Lineage row-level export position

**Current state:** `EfDataLineageService` traces source field → mapping rule → destination column → export file/status/timestamp. It stops there; there is no concept of "which row in that file."

**Plan (largest item — touches every row-oriented destination writer):**
- Add `ExportRowMapping(ExportId Guid, RowNumber int, ResourceRecordId Guid)` entity + table + migration `AddExportRowMapping`.
- Only meaningful for row-oriented formats: Csv, Excel, Parquet, Avro. PDF and raw-JSON-blob destinations don't have a row concept — lineage for those stays at "exported, no row number," which is correct, not a gap.
- Each of those four destination writers (`RelationalDestinationWriterBase` subclasses / the dedicated Csv/Excel/Parquet/Avro writers under `FHIRBridge.Infrastructure/Destinations/`) needs to record `(ExportId, rowIndex, resourceRecordId)` as it writes each row — likely a small `IExportRowTracker` passed into the write loop, batched and flushed at the end rather than one DB write per row (perf).
- `EfDataLineageService.GetLineageAsync` joins to `ExportRowMappings` when present and adds the row number to `DataLineageExportDto`; when absent (non-row format or pre-migration historical exports), the field is simply null and the portal shows "not tracked for this format" rather than fabricating a number.
- Portal: `data-lineage.component.html` renders the row number when present.

**Verification:** scratch test — run a real pipeline execution to a CSV destination, confirm the row mapping lands and lineage reveals the correct row number for a known resource.

**Effort:** Highest of the six. Cross-cutting change across 4 writer classes plus a new table. Recommend doing this one last and re-confirming it's still wanted once items 1/2/4/5 are done, since it's the one most likely to have hidden edge cases (batched writes, retried writes, partial failures mid-export).

---

## Item 4 — SOC2 "Scheduled monthly"

**Current state:** `QuestPdfComplianceReportService.GenerateSoc2EvidenceReportAsync` works and is called on-demand from `GovernanceController`. Nothing schedules it.

**Plan:**
- New `ScheduledReportRun` entity (`ReportType`, `PeriodStart`, `PeriodEnd`, `GeneratedAtUtc`, `FileLocation`, `Status`) + migration.
- New `Soc2ScheduledReportWorker : BackgroundService` in `src/Worker/FHIRBridge.Worker/`, same shape as existing workers (`AlertEvaluationWorker` is the closest recent example) — checks once a day whether the current calendar month's report has already been generated; if not and today is on/after the configured generation day (e.g. 1st of the month, covering the prior month), calls the existing generator, saves the PDF to blob storage, records a `ScheduledReportRun` row.
- New `Soc2ScheduledReportOptions` (`Enabled`, `GenerationDayOfMonth`) registered the same way as `RuntimeWorkerOptions`/`AlertEvaluationOptions`.
- `GovernanceController` gets a `GET /api/v1/governance/compliance-reports/scheduled` endpoint returning recent `ScheduledReportRun` rows so the portal can show "Last generated" / "Next due."
- Portal: `compliance-reports.component.ts` replaces the static "Scheduled monthly / Pending next run" text with real data from that endpoint.

**Verification:** scratch test — force the worker's due-check with a fabricated "current month not yet generated" state against real SQL Server, confirm it generates and records the row once, and does not double-generate on a second run within the same month.

**Effort:** Low-moderate. The hard part (report generation) already exists; this is scheduling + a tracking table, same pattern as three existing workers.

---

## Item 5 — System Health across Api + Worker

**Current state:** `EfSystemHealthService`/`InMemorySystemHealthService` report `"This process (Api/Worker)"` (whichever host served the request) + SQL Server. There's no channel for the Api to learn the Worker's state or vice versa.

**Plan:**
- New `ProcessHealthSnapshot` entity (`ProcessName`, `CpuPercent`, `MemoryBytes`, `RecordedAtUtc`) + migration `AddProcessHealthSnapshots`. One row per process type, upserted (not appended) — this isn't an audit table, so `AuditingSaveChangesInterceptor`'s append-only guard should not apply to it; confirm it's only wired to the governance tables, not this one.
- New lightweight hosted service in the Worker, `ProcessHealthHeartbeatWorker`, reusing the existing `IProcessHealthProvider` (already built for the Api's own snapshot) — every 30s, upserts its own row tagged `"Worker"`.
- `EfSystemHealthService.GetSystemHealthAsync` (Api side) adds a third component: reads the latest `"Worker"` row; if its `RecordedAtUtc` is older than ~90s (3 missed heartbeats), report it as `"Offline"` / `"Stale"` rather than silently showing outdated numbers as healthy.
- The Api's own row can go through the same table for consistency (upsert `"Api"` each request or on a matching timer) rather than staying purely in-process — simplifies `EfSystemHealthService` to "read 3 rows from one table" instead of "compute mine live + guess about the other."

**Verification:** scratch test — start the real Worker process (or simulate via a direct call to the heartbeat upsert), confirm the Api's system-health endpoint picks up a fresh Worker row, then simulate staleness (backdate the row) and confirm it flips to Offline/Stale.

**Effort:** Moderate. One new table, one new tiny worker, a rework of `EfSystemHealthService` from "compute live" to "read heartbeat rows."

---

## Item 6 — Log Settings functional toggles

### 6a. PHI-masking toggle — **out of scope, do not build**

This was deliberately left non-functional. Disabling PHI masking is itself a compliance regression, not a technical gap. No further action recommended here; if the user wants this reconsidered, that's a policy conversation, not an engineering task.

### 6b. Per-tenant payload-logging opt-in with auto-purge — **needs a decision before any code**

Turning this on means storing raw request/response bodies that may contain PHI, per tenant. Before implementing:

**Stop and ask the user (or whoever owns compliance sign-off) explicitly:**
- Should this exist at all, or should the current honest "not implemented" stay as the permanent answer?
- If yes: what's the maximum retention window before auto-purge (the plan below assumes a short one, e.g. 7-30 days, much shorter than the 7-year audit-log retention)?
- Does enabling it for a tenant require its own audit trail entry (who turned it on, when)?

**If approved, the shape would be:**
- Add `PayloadLoggingEnabled bool` + `PayloadLoggingRetentionDays int` to the `Tenant` aggregate's configuration.
- Gate the existing request/response body capture (wherever `ApiRequestLoggingHandler` currently declines to store bodies) behind that per-tenant flag.
- Register a new `GovernanceLogPurgeableStore<T>` for the payload-bearing table with the tenant's configured retention — reuses the exact archive-then-purge mechanism built in Tier 1, just with a shorter, tenant-specific window instead of the fixed global ones.
- `LogSettingsDto`/`LogCategorySettingDto` and the portal screen change from a flat "not implemented" statement to a real per-tenant toggle, but only after the decision above is made.

**Effort:** Moderate engineering, but effort isn't the blocker — the compliance decision is.

---

## Suggested order

1. Item 4 (SOC2 scheduling) — lowest risk, reuses existing pattern, no new cross-cutting concerns.
2. Item 5 (System Health heartbeat) — moderate, self-contained, immediately useful.
3. Item 1 (Archive Restore) — moderate, self-contained, closes a known 501.
4. Item 2 (Exports download tracking) — moderate, touches a handful of destination types.
5. Item 3 (Data Lineage row-level tracking) — highest effort, cross-cutting; do last so any lessons from items 1-2 (which also touch destination writers/storage) carry over.
6. Item 6b (payload-logging opt-in) — **only after explicit compliance sign-off**; not sequenced until that answer comes back.

Item 6a is not sequenced — it's excluded.
