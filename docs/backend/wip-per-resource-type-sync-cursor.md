# WIP: Per-resource-type `_lastUpdated` sync cursor (workflow engine)

Full plan: see conversation / this file supersedes it for live status. Original plan saved at
`C:\Users\Administrator\.claude\plans\frolicking-tumbling-crown.md` (local to the machine that planned it — copy
relevant sections here if that path isn't available to you).

## Why
Search-REST source nodes in the workflow engine (`SourceNodeExecutors.cs`) can fetch multiple resource types in one
node, each via its own FHIR search call — but the incremental `_lastUpdated` watermark
(`SourceRetrievalConfiguration.LastSuccessfulSyncUtc`) is a single `DateTime?` shared by the whole connection. This
means: (1) all resource types share one watermark even though they're fetched independently, and (2) the cursor
advances for every type after a run even if one type was skipped/failed, silently losing its retry window.

Fix: make the watermark a dictionary keyed by resource type, `LastSuccessfulSyncUtcByResourceType`, and only advance
entries for resource types that actually succeeded in a given node run.

**Out of scope:** `SourceConfiguration`-level cursor (per-workflow override) — `RecordRetrievalSync` on that entity is
pre-existing dead code, never called anywhere. Its signature is updated for consistency only, not wired up.
**Out of scope:** bulk export `_since` — a batched export job already covers multiple resource types in one job, so a
single job-level `_since` is correct there.

## Status checklist — ALL DONE, verifying now

- [x] 1. `src/FHIRBridge.Domain/ValueObjects/SourceRetrievalConfiguration.cs` — dictionary property `LastSuccessfulSyncUtcByResourceType`, `GetLastSuccessfulSyncUtc(type)`, `GetEarliestSuccessfulSyncUtc(types)` (for bulk export's single job-level cursor), `WithLastSuccessfulSync(resourceTypes, syncedAtUtc)`
- [x] 2. `src/FHIRBridge.Domain/Entities/SourceConnection.cs` + `SourceConfiguration.cs` — `RecordRetrievalSync(resourceTypes, syncedAtUtc)` signature
- [x] 3. `src/FHIRBridge.Infrastructure/Persistence/Configurations/SourceConnectionConfiguration.cs` + `SourceConfigurationConfiguration.cs` — JSON column mapping (`System.Text.Json` conversion) + `ValueComparer`
- [x] 4. EF migration `20260803085841_AddPerResourceTypeSyncCursor` (both tables) — hand-edited after `dotnet ef migrations add` to reorder (add column → backfill SQL → drop old column) and add backfill/rollback SQL (STRING_AGG/STRING_SPLIT to build JSON, OPENJSON to reverse). Snapshot auto-updated by the EF tool.
- [x] 5. `ISourceConnectionSyncCursorStore` + `SourceConnectionSyncCursorStore.cs` — `RecordSuccessfulSyncAsync(connectionId, resourceTypes, syncedAtUtc, ct)`, single repo round trip, no-ops on empty list
- [x] 6. `FhirSourceConfiguration` DTO (`LastUpdatedWatermarks`) + `SourceConnectionRuntimeResolver.cs` (populate watermarks in `ResolveAsync`; `ComposeSearchParameters` no longer injects `_lastUpdated`; bulk-export `Since` now uses `GetEarliestSuccessfulSyncUtc`)
- [x] 7. `SourceNodeExecutors.cs` — watermark applied inside `SearchWithPolicyAsync` (see IMPORTANT GOTCHA below), `syncedResourceTypes` tracked per-type, cursor store called once post-loop with only the successful types
- [x] 8. `SourceRetrievalConfigurationDto.cs` + `ConfigurationMapper.cs` — dictionary threaded through
- [x] 9. `portal/src/app/source-connections/models/source-connection.model.ts:47` — `lastSuccessfulSyncUtcByResourceType?: Record<string, string> | null`
- [x] 10. Also fixed (not in original plan): `ConfiguredPipelineService.BuildBulkExportRequest` (~line 1198) had its own `retrieval.LastSuccessfulSyncUtc` reference — switched to `GetEarliestSuccessfulSyncUtc(resourceTypes)`. Updated `tests/FHIRBridge.UnitTests/Pipeline/BuildBulkExportRequestTests.cs`'s `Bulk()` helper accordingly.
- [x] 11. New tests added: `tests/FHIRBridge.UnitTests/Sources/SourceRetrievalConfigurationSyncCursorTests.cs`, `SourceConnectionSyncCursorStoreTests.cs`, `tests/FHIRBridge.Runtime.UnitTests/Workflows/SourceNodeExecutorSyncCursorTests.cs`. All targeted filters pass (28 in UnitTests, 19 in Runtime.UnitTests, including the new ones).
- [x] 12. Full `dotnet test FHIRBridge.sln` run. Results: `ArchitectureTests` 6/6 pass, `Runtime.UnitTests` 99/99 pass, `UnitTests` 307/308 pass — the 1 failure (`DestinationExecutionHistoryGateTests.Update_throws_when_destination_has_execution_history`) is PRE-EXISTING, tied to `ConfigurationService.cs`/`IConfigurationService.cs`, which were already modified/uncommitted before this task started (unrelated mapping-profile-master-screen work in progress) — not caused by this change. `Api.IntegrationTests` all fail with `ASPNETCORE_URLS is not set` — a pre-existing environment/host-factory issue unrelated to this change, reproduces even on unrelated tests.
- [ ] 13. (User to run manually, not me) `dotnet ef database update` against a real DB — migration has NOT been applied anywhere.

## THIS TASK IS FUNCTIONALLY COMPLETE
Everything in the plan is implemented, builds clean, and passing tests confirm it. Remaining open item is purely
operational: applying the migration to a real database, which the user (not me) should run when ready.

Note: during this session, `FHIRBridge.Api` (PID 27880, running under Visual Studio) and a stray `FHIRBridge.Worker`
(PID 33324) were killed with explicit user approval to unlock build output for `dotnet ef migrations add`. If you
need to resume Visual Studio debugging, restart it from there.

## IMPORTANT GOTCHA discovered during implementation
`SourceNodeExecutors.SearchCohortScopedAsync` (~line 551) deliberately does
`var batchSource = source with { PatientIds = batch, TargetPatientId = null, SearchParameters = null };`
for every non-Patient sibling resource type once a Patient cohort exists — it wipes `SearchParameters` on purpose
(the connection's Patient-only search criteria doesn't apply to siblings). My first attempt built a per-type
`typeSource` with `_lastUpdated` baked into `SearchParameters` *before* dispatch in the `ExecuteAsync` loop — this
silently got wiped by the line above for any cohort-scoped sibling type (caught by a test that exercises this path;
initially failed with a `NullReferenceException`-adjacent `ArgumentNullException` in `SearchCohortScopedAsync`).

**Fix applied**: the `_lastUpdated` injection was moved into `SearchWithPolicyAsync` (~line 701) — the single choke
point both the direct-search and cohort-scoped-search paths funnel through — reading `source.LastUpdatedWatermarks`
(which is NOT cleared by the cohort `with` above, only `SearchParameters`/`PatientIds`/`TargetPatientId` are) and
appending `_lastUpdated` fresh onto whatever `SearchParameters` the caller passed in at that point. `ExecuteAsync`'s
loop just passes `source` straight through to `SearchWithPolicyAsync`/`SearchCohortScopedAsync` again, unchanged from
before this feature. If you're picking this up mid-way and see a `typeSource` variable in the loop or a watermark
baked into `SearchParameters` upstream of `SearchWithPolicyAsync`, that's the bug — undo it.

## Notes for continuation
- Migration file: `src/FHIRBridge.Infrastructure/Persistence/Migrations/20260803085841_AddPerResourceTypeSyncCursor.cs` (already hand-edited, do not regenerate).
- New column name: `RetrievalLastSuccessfulSyncByResourceType` (nvarchar(max), JSON), replacing `RetrievalLastSuccessfulSyncUtc` (datetime2) on both `SourceConnections` and `SourceConfigurations` tables.
- `AppendSearchParameter` helper lives near the other private static helpers in `SourceNodeExecutors.cs` (search for it).
- The FHIRBridge.Api process (PID 27880, running under VS) and a stray FHIRBridge.Worker (PID 33324) were killed during this session to unlock build output for `dotnet ef migrations add` — the user approved this. If picking up later, you may need to restart them from Visual Studio.
