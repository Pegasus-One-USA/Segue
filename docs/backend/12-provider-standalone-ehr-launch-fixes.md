# Provider Standalone / EHR Launch / Destination Mapping Fixes — 2026-07-21

Branch: `feature/governancelogging`

This document records a session of bug fixes to the Provider Standalone launch flow, the EHR (embedded) launch flow, and destination field mapping, split into code changes (in this branch) and direct database data fixes (applied only to the local dev DB, **not captured by any migration or seed script**). Read the "Database data fixes" section before merging this branch or standing up a fresh environment — those changes need to be reproduced manually or converted into a migration/seed update.

## Code changes

### FHIRBridge API (backend, C#)

- `src/Runtime/FHIRBridge.Runtime.Infrastructure/Workflows/Executors/SourceNodeExecutors.cs`
  - `CohortBatchSize` changed from `50` to `1`. Epic (and US Core generally) rejects a clinical-resource search scoped to more than one patient in a single request ("A given request can only apply to one patient") — cohort-scoped searches now issue one request per patient instead of one batched, comma-joined request.
  - Added `RestrictToDestinationResourceTypesAsync` and a new optional `IWorkflowDefinitionStore` constructor dependency on `SourceNodeExecutor` and all 8 concrete subclasses (Epic/Cerner/EClinicalWorks/Athenahealth/Allscripts/Meditech/GenericFhir/Sample). A source node's fetch is now narrowed to the union of resource types actually selected by its reachable downstream destination node(s) in the workflow graph, instead of always fetching everything listed in its own `Resources` field (previously caused silent over-fetching, and over-broad OAuth scope requests, whenever a destination wizard's resource selection was narrower than the source's configured list).

- `src/FHIRBridge.Infrastructure/Sources/SourceConnectionRuntimeResolver.cs`
  - Added `IScopeGeneratorService` dependency. When a `SourceConnection`'s persisted `Authentication.Scopes` is empty, scopes are now generated live from the connection's `ApplicationType` + configured resource types (reusing the same generator the wizard and the scope-resync endpoint use), instead of silently falling back to a hardcoded Patient-shaped default (`launch/patient patient/*.read offline_access`) regardless of the connection's real application type. That hardcoded fallback previously forced Epic's native "Search for a Patient" screen for empty-scope Standalone/EHR-launch connections.

- `tests/FHIRBridge.Runtime.UnitTests/Workflows/SourceNodeExecutorCohortTests.cs` — updated to assert per-call (not last-call-wins) patient scoping, matching the `CohortBatchSize=1` change; total resource count assertion changed 4 → 6.
- `tests/FHIRBridge.Runtime.UnitTests/Workflows/SourceNodeExecutorDestinationRestrictionTests.cs` — **new file** — two tests covering the destination-resource-type restriction feature (fetch is restricted when a destination is reachable in the graph; unrestricted when none is).

### FHIRBridge Portal (Angular)

- `portal/src/app/components/node-library/destination-wizard/destination-wizard.component.ts`
  - `updateRow()`: the mandatory Id/upsert-key row's destination-column edit guard now only blocks writes when `isIdColumnLocked(...)` is true (a real primary/unique key was schema-detected on the destination table). Previously the guard unconditionally discarded any edit to the Id row's column, even in the case where no PK/unique key was detected and the template renders a live "choose the column that uniquely identifies each {Resource}" picker specifically so the user can pick one — meaning any column the user selected there was silently thrown away in favor of a catalog-derived default column name.

### HealthApp (Demo_TestApp API / UI)

No code changes. Investigated (read-only) to trace request/redirect flows: `Demo_TestApp/frontend/src/app/demo-types/provider-standalone/launch-standalone-provider.ts`, `Demo_TestApp/frontend/src/app/demo-types/demo-type-2/launch-provider-in-app.ts`, `Demo_TestApp/frontend/src/app/app.ts`, `app.routes.ts`, `Demo_TestApp/backend/Program.cs`, `Demo_TestApp/backend/HealthAppDbContext.cs`. All fixes affecting HealthApp's actual behavior were made via FHIRBridge-side database configuration (below), not HealthApp code.

## Database data fixes (dev DB only — not in git, not in any migration)

Applied directly against the local SQL Server dev database (`fhirbridge-controlplane-sql` container, `FHIRBridge` DB).

**`SourceConnections` table:**

| Connection | Field | Change | Reason |
|---|---|---|---|
| `b496d371-672e-4ff7-b266-8b4e07fa3938` ("Epic", Standalone) | `Scopes` | Removed stale `launch/patient` scope | Regression from an earlier scope-generator bug window (~2026-07-08 to 2026-07-16); was forcing Epic's native patient-picker instead of going straight to consent for Provider Standalone. |
| `dca9230b-b3e0-44dd-8132-ee10deeb388e` ("Epic-1", EhrLaunch) | `PostLaunchRedirectUri` | `NULL` → `http://localhost:5501/launchproviderinapp` | Without this, the OAuth callback after a genuine EHR launch had nowhere to redirect the browser back to; HealthApp's `LaunchProviderInAppComponent` expects to land back on exactly this path. |

**`WorkflowNodes` table (`ConfigurationJson`):**

| Node | Workflow | Field | Change | Reason |
|---|---|---|---|---|
| `F3C470C6-463B-4261-8CC4-E5640347D067` (EpicSourceNode) | `fd12224e-9af4-4ad4-954b-148ad26e1754` | `sourceConnectionId` | Net unchanged (Epic-1) — was briefly repointed away and reverted within this session | This is the genuine, working EHR-launch entry point; confirmed via `SmartLaunchLogs` history. Flagging because it was mistakenly repointed once during this session based on an incorrect diagnosis, then reverted. |
| `8475A1CB-E84C-4F6E-82E5-BC1F78E6325C` ("Epic-2") | `79d5bcd7-c616-4894-9b14-fd9c3f13d150` (Patient Detail) | `sourceConnectionId` | `1bdaf951-74b7-4d2d-831b-264531f0077c` → `b496d371-672e-4ff7-b266-8b4e07fa3938` | Old value pointed at a connection that never had an authorized token; new value is the connection actually signed into via the Fetch Patient List workflow. Root cause: this dev DB has 5 separate Epic `SourceConnection` rows, 3 of which share the same real Epic Client ID but keep separate FHIRBridge-side token caches (keyed by SourceConnectionId). |
| `4F53272F-1CD8-4F82-86ED-4407B1B7D6F7` (CsvDestinationNode) | `a0de009e-9a60-494f-9ff8-d83cefdd1a3b` | `dest_deliveryMode` | `"email"` → `"downloadurl"` | Patient Standalone CSV export wasn't returning a download link (was configured for email delivery instead). |
| `3A3D210D-9914-4CD7-9491-B5D89A796BD2` (SqlServerDestinationNode) | `79d5bcd7-...` | `fields[0].targetField`, `dest_mappings[0].column` | `"Id"` → `"ConditionId"` | `dbo.Condition` in HealthAppDB has no `Id` column, only `ConditionId`. |

**`MappingFields` table:**

| Row | MappingProfileId | Field | Change | Reason |
|---|---|---|---|---|
| `37DC561C-2A82-47DA-95DD-08DEE6EE4DF2` | `3987fbed-014e-4d5f-9245-0755c86ba1a2` (Condition/Id) | `TargetField` | `"Id"` → `"ConditionId"` | This was the fix that actually resolved the `Invalid column name 'Id'` SQL error — the WorkflowNodes JSON fix above was necessary but not sufficient. |

> **Architecture note:** `MappingFields` (referenced by a Runtime-Plane `MappingNode`'s `mappingProfileId`) is the *actual* runtime source of truth for destination column names whenever a `MappingNode` precedes a destination node — **not** the destination node's own `ConfigurationJson.fields`/`dest_mappings`. Those are read only as a fallback for table-existence validation and upsert-key-*name* resolution, in `DestinationNodeExecutor.CreateMappingProfile` (`src/Runtime/FHIRBridge.Runtime.Infrastructure/Workflows/Executors/DestinationNodeExecutors.cs`). See `MappingNodeExecutor.ExecuteAsync` in `TransformNodeExecutors.cs`, which prefers `_configurationRepository.GetMappingProfileAsync(profileId, ...)` over the node's own `"fields"` config whenever `mappingProfileId` is set. This distinction cost significant debugging time and is worth remembering for any similar "wrong column name" investigation.

## Before merging to main

- Convert the database data fixes above into either an EF Core migration/seed-data update, or a documented admin runbook step — as-is they only exist in this one dev database and will be lost on a fresh environment.
- Consider auditing for other Epic `SourceConnection` rows sharing a Client ID with separate token caches (the same class of bug hit twice this session — see the Epic-2/Epic table row above).
