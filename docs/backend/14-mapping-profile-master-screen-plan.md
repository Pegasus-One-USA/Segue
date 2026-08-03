# 14 — Mapping Profile Master Screen Plan

**Status:** proposed, not started
**Goal:** promote mapping profiles to a master screen under Settings (peer of Source Connections and Destination
Connections), and let the destination wizard **auto-populate field mappings from saved profiles at the resource
selection step** instead of authoring every mapping from scratch.

---

## 1. Current state (verified against code)

### Backend — the entity exists, the portal has never called it

`MappingProfile` (`src/FHIRBridge.Domain/Entities/MappingProfile.cs`) is fully persisted with audit support
(`AuditableChildEntity<Guid>`, `IHasAuditDisplayName`), an owned `MappingField` collection, and soft-delete
capability from the `ISoftDeletable` global filter (`FHIRBridgeDbContext.cs:88-97`).

Four endpoints exist, all blanket `AuthorizationPolicies.UnifiedAdmin`:

| Verb | Route | File |
|---|---|---|
| POST | `/api/v1/mapping-profiles` | `Controllers/V1/ConfigurationsController.cs:215` |
| PUT | `/api/v1/mapping-profiles/{mappingProfileId:guid}` | `ConfigurationsController.cs:227` |
| POST | `/api/v1/mapping-profiles/{mappingProfileId:guid}/deactivate` | `ConfigurationsController.cs:243` |
| GET | `/api/v1/mapping-profiles` (unpaged list-all) | `ConfigurationCatalogController.cs:131` |

`grep -rn "mapping-profiles" portal/src` returns **zero hits**. Profiles are created only as a side effect of
`POST /api/v1/workflows/build` (`WorkflowEndpoints.cs:185-211`).

### The three model layers

| Layer | Scope | Entity? |
|---|---|---|
| `MappingProfile` | **one resource type**, field-level | yes |
| `ResourcePipelineRoute.ResourceMappings` | the composed set — `ExecutionOrder`, `SearchParameters`, `ParentReferences` | yes, but route-scoped |
| Node config `mappingProfileIds` | `{resourceType → profileId}` canvas mirror | no, just a config map |

`MappingProfile.ResourceType` is a single non-nullable string, so **one profile can never span resources**. One
wizard authoring pass over Patient + Observation + Condition produces **three** profiles — the assembler says so
explicitly (`workflow-build-assembler.service.ts:492-498`).

### Frontend — mapping lives inside one wizard step

All authoring is step 3 of `components/node-library/destination-wizard/destination-wizard.component.ts`
(1436 lines; `TOTAL_STEPS = 4`, `STEP_LABELS = ['Configure', 'Data groups', 'Map fields', 'Review']` at `:169-170`).

Step boundaries in the template: step 1 `:39`, step 2 `:348-377`, step 3 `:380-550`, step 4 `:551`.
**Step 2 is 31 lines of tick-boxes.** Step 3 is 170 lines carrying three jobs at once: per-resource target names,
parent-reference topology (`toggleParent` at `:444`), and every mapping row.

### The pattern to copy

Destination Connections is the reference master screen: route child in `settings/settings.routes.ts:48-56`,
tab entry in `SETTINGS_TABS` (`settings/layout/settings-shell.component.ts:20`), list page + `MatDialog` dialog
with `mode: 'create' | 'edit' | 'view'`, service, and `models/`. Critically, its dialog **embeds the wizard's own
form component** (`DestinationConnectionFormComponent`) via `viewChild()` and calls `getConfig()` on it. That is
the trick this plan reuses.

---

## 2. Destination families: who owns the target schema

The wizard already splits destinations three ways (`destination-wizard.component.ts:261-281`), and this decides
what a "target field" even means:

| Category | Types | Target fields come from | Verifiable |
|---|---|---|---|
| **Destination-owned** | SqlServer, AzureSql, PostgreSql, MySql | live introspection (`POST /destinations/schema-preview`) | yes |
| Destination-owned, probe missing | Snowflake, Databricks | should be introspected; no probe implemented | not today |
| **Mapping-owned** | Csv, Excel, Ndjson, Parquet, Avro, Protobuf, Pdf, Sftp, Blob, S3, Mongo, InMemory | free text — you declare them | no |
| **Spec-owned** | FhirRepository (+ future HL7v2 out, C-CDA, registries) | fixed by an external standard | by conformance, not introspection |

Evidence: the probe gate fires only for SQL (`:441`, `:457`, `:659`); the Mongo form notes "no live probe to
validate a split server/database/credentials form against" (`:196`); the auto-matcher falls back to naming
convention when "No live schema loaded (CSV, or SQL not yet probed)" (`:849`); `MappedDestinationSerialization.cs:70`
derives tabular output columns from "the union of mapped value keys" — i.e. for files the mapping *is* the schema.

Two consequences:

- **Mapping-owned profiles can be authored with no connection, no credentials, no network.** That is the cheapest
  first slice.
- Model this as a derived enum, **not** a bool. `TargetSchemaOwnership` resolved through a registry keyed by
  `DestinationType` — never stored on `MappingProfile` (it would go stale if a destination is retyped), and never
  a `switch` (CLAUDE.md / architecture tests).

---

## 3. The headline feature: auto-populate at resource selection

### Why step 2 and not step 3

- One profile = one resource type, and step 2 is the only per-resource-type control in the wizard.
- Step 2 is nearly empty; step 3 is overloaded. Choosing here *removes* work from step 3.
- The probe has already run (step 1 gates on it for SQL, `:441`/`:647`), so a selected profile can be validated
  against real columns immediately.
- A selected profile **supplies** `destinationObject`, so the target name is populated rather than typed.
- If every ticked resource resolves to a profile, **step 3 becomes a review instead of data entry.** That is the
  actual payoff — a picker buried in step 3 saves typing; a picker here can save the whole step.

### UI

Step 2 gains a section-level toggle plus a per-card selector:

```
☑ Use saved mapping profiles                      [ 4 of 6 resources matched ]

[✓] Patient       Mapping: [ Standard Patient Export  ▾ ]   12 fields ✓
[✓] Observation   Mapping: [ Obs → ObservationFact    ▾ ]   ⚠ 2 columns not found
[✓] Condition     Mapping: [ — author new —           ▾ ]   no saved profile
[ ] Encounter
```

### New component state

```ts
readonly useSavedProfiles      = signal(false);
readonly savedProfilesByResource = signal<Record<string, MappingProfileDto[]>>({});
readonly chosenProfileByResource = signal<Record<string, string | null>>({});
readonly profileWarningsByResource = signal<Record<string, string[]>>({});
```

### Candidate loading — one request, not N

On entering step 2, fetch once from the new paged endpoint and group client-side by `resourceType`:

- **Destination-owned:** filter `sourceConnectionId` + `destinationId`. Both must already exist (see §3.1).
- **Mapping-owned / spec-owned:** filter on `isEnabled` only; group by `resourceType`. The destination binding is
  not load-bearing, so any profile for that resource type is a valid starting point.

### Auto-populate algorithm

When `useSavedProfiles` is switched on, and again whenever a resource is newly ticked while it is on:

1. Look up `savedProfilesByResource[resource]`.
2. Zero candidates → leave as "author new", label the card `no saved profile`.
3. Exactly one → select it. Several → select the most recently modified **and show the count**, so the choice is
   never silent.
4. `targetByResource[resource] = profile.destinationObject`.
5. Translate `profile.fields[]` → `MappingRow[]` (see §3.2) and replace that resource's rows in `mappingRows`.
6. Re-run `_reconcileIdRows()` (`:1065`) and `_reconcileParentRefRows()` (`:1134`) — a saved profile may not carry
   the id row or the parent refs this workflow's topology needs.
7. Validate (see §3.3) and write any messages to `profileWarningsByResource`.
8. Snapshot a per-resource baseline for the changed/unchanged decision at save.

Unticking a resource clears its rows, its chosen profile, and its warnings.

### 3.1 Availability gate — read this before committing to the coupling

`MappingProfile.SourceConnectionId` and `DestinationId` are non-nullable. A saved profile can therefore only match
a source and destination that **already exist**, which happens only when both were picked as *existing* in this
workflow:

| Source | Destination | Destination-owned | Mapping-owned |
|---|---|---|---|
| existing | existing | available | available |
| existing | new | **empty** | available |
| new | existing | **empty** | available |
| new | new | **empty** | available |

```ts
canUseSavedProfiles = isMappingOwned() || (sourceResolvedExisting() && destinationResolvedExisting());
```

So under strict coupling the feature is dark in three of four cases for SQL destinations — the common
"building a fresh pipeline" path. For mapping-owned destinations it works in all four. This is the practical
argument for relaxing the coupling later (§8b); until then, render an inline explanation on the card
("no saved Patient mapping for this source + destination") rather than a mystery disabled control.

**Prerequisite:** the destination wizard cannot currently see the upstream source connection id — it lives on the
source node's config (`sourceConnectionId`, `sourceConnectionResolved`) and only the assembler reads it, at build
time. Filtering by source needs the wizard to read upstream node config from `services/pipeline.store.ts`. New
coupling, small but real.

### 3.2 `MappingRow` must be widened first — data-loss trap

`MappingFieldDto` has **22 members**. Today's `MappingRow` (`:95-114`) carries about **7**. If a profile authored
in the Settings screen with advanced fields set is pulled through the wizard as-is, the round trip through
`dest_mappings` **silently drops** `IsRequired`, `DefaultValue`, `Format`, `NormalizationType`,
`TerminologySystemJsonPath`, `TerminologyCodeJsonPath`, `ArrayPolicy`, `Cardinality`, `CorrelationCodeJsonPath`,
`CorrelationCodeValue`, and per-field `IsEnabled`.

That includes the `CorrelateByCode` pair — the DTO's own example is `"8480-6"` for BP systolic. Losing it silently
breaks blood-pressure component mapping.

So widening `MappingRow` to carry all persisted members, and extending the `dest_mappings` serializer
(`:1409-1427`) and loader (`:1300-1330`) in lockstep, is a **hard prerequisite** of auto-populate — not an
enhancement.

Never round-trip `MaxLength`, `Precision`, `Scale`: the DTO states they are "never persisted on the mapping profile
itself, only filled in at pipeline run time" from live schema.

Translation map:

| `MappingFieldDto` | `MappingRow` |
|---|---|
| `TargetField` | `targetName` |
| `JsonPath` | `jsonPath`; resolve `fieldLabel`/`fhirPath` from the catalog by matching, else fall back to the raw path |
| `ValueType` | `valueType` |
| `ArrayAncestors` | `arrays` |
| `IsUpsertKey` | `isUpsertKey` |
| profile `ResourceType` / `DestinationObject` | `resource` / `tableName` |
| all advanced members | **new `MappingRow` fields** |

### 3.3 Validation on selection, per category

- **Destination-owned:** every `TargetField` must appear in `columnsForResourceTarget(resource)` (`:676`, which
  already filters out `isAutoGenerated`). Reuse `isRowColumnUnverified` (`:999`) and `isRowTypeMismatched`
  (`:1018`) to badge the card. Validate at step 2, not step 3 — otherwise the user picks here and discovers
  breakage there.
- **Mapping-owned:** nothing to verify. Apply the rows.
- **Spec-owned:** defer to the existing US-Core/profile validation path; out of scope for slice 1.

### 3.4 Save semantics — reuse the source/destination precedent

Both existing pickers already solve shared mutation: untouched → reuse by id; edited → fork with a deduped name
(`hasExistingChanged()` / `_resolveUniqueName()` at `:631`, `:646`). `MappingBuildSpec.ExistingId` already exists
(`WorkflowBuildModels.cs:62`) and means **update in place**, and the portal already reads ids back via
`parseExistingMappingProfileIds` (`:510`).

Apply the same rule **per resource**:

| User action | Emitted | Result |
|---|---|---|
| Profile selected, rows untouched | `ExistingId` = that profile | Links to it; in-place update is a no-op |
| Profile selected, rows edited | `ExistingId` omitted | New profile created, master untouched |
| No profile selected | `ExistingId` omitted | Today's behaviour |

Zero backend change. One wart: reuse-untouched still bumps `ModifiedOnUtc`/`ModifiedBy` on a shared row on every
save. A `SkipUpdate` flag on `MappingBuildSpec` would fix the audit noise if it matters.

### 3.5 Edit mode — gate per resource, not per wizard

Precedent is blanket create-only (`showSourcePicker` uses `!isEditing`; `showConnectionModeToggle` is
`!editNode()` at `:259`). That is **too coarse here**, because resources can still be ticked in edit mode
(only destination *type* is locked, via `destTypeLocked` at `:169`), and a newly ticked resource has no profile.

| Resource state | Picker |
|---|---|
| Already has an id in `mappingProfileIds` | hidden — show "editing this workflow's mapping" |
| Newly ticked this session (no id) | shown, same as create |

Cheap: the node config already carries `mappingProfileIds` keyed by resource type.

Keep *repoint* out of edit mode (it orphans the workflow's own profile and makes later edits mutate the shared
master). A separate *reapply* action — keep `ExistingId` pinned to the workflow's own profile, replace only the
rows — is safe and useful, but out of slice 1. Label the create-mode control **"Start from saved mapping"** so
adding "Reapply from…" later doesn't read as contradictory.

---

## 4. Backend work

### 4.1 Audit fields on the DTO

`DTOs/MappingProfileDto.cs` — append `CreatedOnUtc`, `CreatedBy`, `ModifiedOnUtc`, `ModifiedBy`, matching
`DestinationConfigurationDto`. Populate in `Mappings/ConfigurationMapper.cs:144-158` (the entity already has all
four).

⚠ The record ends with optional `Guid? SourceConfigurationId = null`. New required positional members must go
**before** it. Audit every `new MappingProfileDto(...)` call site after the change.

### 4.2 Display-name resolution on list

`ConfigurationCatalogController.cs:131` returns raw DTOs. Apply the `_userDisplayNameResolver.ResolveAsync` +
`dto with { ... }` treatment `ListDestinations` already uses (`:83-98`).

### 4.3 Paged list

Mirror the destination triplet exactly:

- `IConfigurationRepository` (`:59-63`): add
  `Task<PagedResult<MappingProfile>> GetMappingProfilesPagedAsync(MappingProfileFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct);`
- New filter beside `DestinationFilter`:
  `public sealed record MappingProfileFilter(string? Search, string? ResourceType, Guid? SourceConnectionId, Guid? DestinationId, bool? IsEnabled);`
- Implement in **both** `EfConfigurationRepository.cs` (near `:274-313`) and `InMemoryConfigurationRepository.cs`
  (near `:225-245`) — the in-memory one backs integration tests.
- `IConfigurationService` / `ConfigurationService.cs:437-500`: `GetMappingProfilesPagedAsync`.
- `ConfigurationCatalogController`: `GET /api/v1/mapping-profiles/paged`, following `ListDestinationsPaged`
  (`:100-121`) including the `page <= 0 ? 1` / `pageSize <= 0 ? 25` defaults.

Sort whitelist: `name`, `resourceType`, `destinationObject`, `isEnabled`, `createdOnUtc`, `modifiedOnUtc`.
Eager-load `Fields` so the grid can show a field count. **This endpoint is what step 2 calls** — the
`sourceConnectionId` + `destinationId` + `resourceType` filters exist for it.

### 4.4 Get by id

`GET /api/v1/mapping-profiles/{mappingProfileId:guid}`, 404 when absent.
`IConfigurationRepository.GetMappingProfileAsync` already exists; this needs the controller action plus a thin
service passthrough for display-name resolution.

### 4.5 Usage endpoint

`GET /api/v1/workflows/mapping-profile-usage` beside `destination-usage` (`WorkflowEndpoints.cs:473`), returning
profile ids referenced by any `ResourcePipelineRoute.MappingProfileId`,
`ResourcePipelineRouteMapping.MappingProfileId`, `ParentReferenceLink.ParentMappingProfileId`, or node config
(`mappingProfileId` / `mappingProfileIds`). Drives Edit/Delete gating and a "used by N workflows" warning.

### 4.6 Activate

`SetMappingProfileEnabledAsync` already takes a `bool`; only `false` is reachable. Add
`POST /api/v1/mapping-profiles/{mappingProfileId:guid}/activate`. One controller action.

### 4.7 Delete — promoted into scope

There is **no delete path anywhere**: `IConfigurationRepository` exposes only get/get-by-id/add/update, and
neither repository implementation has a remove method. Consequence: topology changes in `/workflows/build`
strand rows, and `DELETE /api/v1/workflows/{id}` never cleans them up.

This was going to be deferred, but the master screen makes the orphans **visible on day one** — the first thing
users see is a list containing junk. So:

- `DeleteMappingProfileAsync` on repository + service (soft delete, honouring the `ISoftDeletable` filter)
- `DELETE /api/v1/mapping-profiles/{id}` → 204 / 404 / **409 when in use**, guarded by §4.5
- A usage guard is mandatory before delete: `ResourcePipelineRoutes.MappingProfileId` and
  `ResourcePipelineRouteMappings.MappingProfileId` are `DeleteBehavior.Restrict`
  (`ResourcePipelineRouteConfiguration.cs:41-44, 59-62`)
- A one-off cleanup for pre-existing orphans

---

## 5. Frontend work

### 5.1 Extract the mapping editor (prerequisite for everything else)

New `components/node-library/destination-wizard/mapping-profile-form.component.ts`, sibling of
`destination-connection-form.component.ts` so the existing embed pattern holds.

Move out of `destination-wizard.component.ts`: `MappingRow` (`:95`), `DEST_RESOURCE_DEFS` (`:39`),
`genericResourceDef` (`:116`), `mappingRows` (`:238`), `parentSelections` (`:235`), `targetByResource`,
`addRow` (`:805`), `addFields` (`:833`), `removeRow` (`:921`), `updateRow` (`:789`),
`changeBusinessField` (`:1209`), `_reconcileIdRows` (`:1065`), `_reconcileParentRefRows` (`:1134`),
`_matchScore` (`:892`), `_nameSimilarity` (`:905`), `isRowTypeMismatched` (`:1018`),
`isRowColumnUnverified` (`:999`), `allColumnsAutoGenerated` (`:686`).

```ts
readonly resources   = input.required<string[]>();
readonly schema      = input<DestinationSchema | null>(null);
readonly ownership   = input.required<TargetSchemaOwnership>();
readonly initialRows = input<MappingRow[]>([]);
readonly readonly    = input(false);
getRows(): MappingRow[] | null;   // null ⇒ invalid, mirrors getConfig()
```

Node-config serialization stays in the wizard — the extracted component must know nothing about
`dest_mappings`. That boundary is what lets the Settings dialog reuse it.

**Do this as its own commit with no behaviour change**, bundled with the §3.2 `MappingRow` widening, and verify
the wizard round-trips (`:1300-1330` load, `:1409-1427` save) before building on it. This is the riskiest step.

### 5.2 New `mapping-profiles` feature folder

Mirroring `destination-connections/`:

```
portal/src/app/mapping-profiles/
  models/mapping-profile.model.ts
  services/mapping-profile.service.ts
  pages/mapping-profile-list/
  dialogs/mapping-profile-dialog/     # mode: 'create'|'edit'|'view', embeds MappingProfileFormComponent
```

Add `MAPPING_PROFILE_ENDPOINTS` to `core/api-endpoints.ts` — services never inline URLs.

**Grid:** grouped by `(sourceConnectionId, destinationId)`, expandable to per-resource profiles. This gives the
intuitive "the mapping I built" view without inventing an entity — derivable from data you already have. Slightly
lossy: two separate authoring passes to the same pair merge into one group. Columns: Name, Resource Type, Source,
Destination, Target Object, Fields (count), Enabled, Modified By/On, actions.

**Dialog header fields:** Name (required), Resource Type (required, drives the field catalog), Source Connection,
Destination, Destination Object (table dropdown / free text by category), Enabled. `SourceConfigurationId` stays
hidden — the service auto-provisions it when null.

**Dialog rows:** core columns always visible (FHIR field, target field, value type, upsert key, enabled) plus a
per-row expander for the advanced members from §3.2 — the differentiator over the wizard, since none of them are
reachable there today.

### 5.3 Wire into Settings

- `settings/settings.routes.ts`: child `path: 'mapping-profiles'`, `canActivate: [permissionGuard]`,
  `data: { permissions: ['configuration.write'] }`, after `destination-connections`.
- `settings/layout/settings-shell.component.ts` `SETTINGS_TABS`:
  `{ label: 'Mapping Profiles', route: 'mapping-profiles', icon: 'swap_horiz', permissions: ['configuration.write'] }`
  after Destination Connections, so the order reads source → destination → mapping.
- Keep the sidebar visibility rule in sync (the contract is documented at `settings-shell.component.ts:38-39`).

### 5.4 Step 2 auto-populate

Per §3. Then make step 3 render as review-first when every ticked resource resolved cleanly.

### 5.5 Field Mapping node dialog

`field-mapping` is a real canvas node (`data/transforms.data.ts:11`, rank 6) but `selectItem` only opens forms for
`epic` and the five `dest-*` ids — **it is the only node with no authoring UI**. Its config already stores
`mappingProfileIds`, so it maps 1:1 to the master entity.

Give it a dialog hosting the same extracted component. End state — one component, three hosts, exactly mirroring
`DestinationConnectionFormComponent`:

| Host | Component |
|---|---|
| Settings → Mapping Profiles dialog | `MappingProfileFormComponent` |
| Destination wizard step 3 | `MappingProfileFormComponent` |
| Field Mapping node dialog | `MappingProfileFormComponent` |

---

## 6. Sequencing

| Slice | Contents | Ships value |
|---|---|---|
| 1 | §4.1–4.2 (audit fields, display names) | independently |
| 2 | §4.3–4.4 (paged list, get-by-id) | independently |
| 3 | §5.1 + §3.2 (extraction **and** `MappingRow` widening, no behaviour change) | gate: wizard regression pass |
| 4 | §5.2–5.3 Settings screen — **mapping-owned (CSV/Mongo) first**, authorable with no connection | first user-visible win |
| 5 | §4.5–4.7 (usage, activate, delete + orphan cleanup) | before the junk is noticed |
| 6 | §5.4 step 2 auto-populate, mapping-owned only | the headline feature |
| 7 | §5.4 extended to destination-owned (needs the §3.1 source-id prerequisite) | |
| 8 | §5.5 Field Mapping node dialog | |

Slices 1–2 and 3 are independent and can run in parallel. Slice 4 needs **no backend change beyond 1–2** and no
wizard change at all: a CSV destination is already creatable offline in Settings, so an offline mapping profile
against it closes the loop.

---

## 7. Tests

- `FHIRBridge.UnitTests` — paging/sort/filter of `GetMappingProfilesPagedAsync` against
  `InMemoryConfigurationRepository`; sort-whitelist rejection; delete usage-guard returns 409.
- `FHIRBridge.Api.IntegrationTests` — new endpoints' status codes, 404 on unknown id, auth policy,
  `CreatedBy`/`ModifiedBy` returned resolved rather than raw ids.
- `FHIRBridge.ArchitectureTests` — must pass untouched; no new cross-layer references, no `switch` on
  `DestinationType` introduced by the ownership registry.
- Portal — specs for the extracted component covering `_reconcileIdRows`, the auto-matcher, and **a full
  `MappingFieldDto` → `MappingRow` → `dest_mappings` → `MappingFieldDto` round trip asserting no advanced member
  is dropped** (§3.2). `mapping-catalog.service.spec.ts` is currently the only mapping-related portal test.

---

## 8. Open decisions

**a) Workflow copy shares profile ids.** `POST /api/v1/workflows/{id}/copy` (`WorkflowEndpoints.cs:592-641`)
clones nodes verbatim, so the copy carries the *same* `mappingProfileIds` — editing the copy's mapping mutates
the original's profile. A live bug independent of this work; arguably fix first, since a clone operation serves
both.

**b) Relaxing the source/destination coupling.** Per §3.1 the feature is dark for most SQL cases. Making
`DestinationId` nullable turns profiles into reusable templates, but collides with `ResourcePipelineRoute`
deriving resource type, source, and destination *from* the mapping, plus `ConfiguredPipelineService`
(`:1118-1151`, `:405, 501, 585, 1038-1039, 1095`). A per-category `RequiresDestinationBinding` — binding only
where it earns its place (destination-owned) — is likely better than one rule for everything.

**c) Name uniqueness.** No unique index on `Name`, no `ExistsWithNameAsync`. Duplicates are legal and likely,
since `/workflows/build` mints one profile per resource per destination. A constraint needs a dedupe migration.

**d) Granular RBAC.** All mapping endpoints are blanket `UnifiedAdmin`; source connections use
`[StandardPermission(PermissionGroupCode.SourceConnections, ...)]`. This plan reuses `configuration.write` to
avoid new permission codes; a dedicated group is the consistent end state (`06-adding-permissions-howto.md`).

**e) Duplicated mapping state in the workflow path — the deepest issue.**
`DestinationNodeExecutors.cs:583, 601, 739-753` rebuilds transient `MappingProfile` instances inline from node
config (`new MappingProfile(..., Guid.Empty, Guid.Empty, ...)`), while `TransformNodeExecutors.cs:158-206`
resolves the persisted row by id. Field mapping is therefore stored **twice** — in `WorkflowNodeConfigurations`
JSON and as a DB row — with only the transform node reading the row. The master screen edits the row; whether
that edit reaches the destination executor depends on this inconsistency. **This must be resolved before the
master screen can honestly claim to be the source of truth**, and it is the strongest argument for (b).

**f) Parent references.** `ParentReferenceLink.ParentMappingProfileId` is route-level, but the wizard resolves
parent topology at authoring time because that is the only place it has a UI. Options: add a parent field to the
profile (duplicates route state), leave it route-owned (wizard- and Settings-authored profiles then differ), or
show it read-only. Leaning route-owned.

**g) `AuditableChildEntity`.** `MappingProfile` still derives from the child-entity base — a leftover of the
removed `Tenant` aggregate (`src/FHIRBridge.Domain/README.md:22`). Harmless today; revisit if promoted to a
genuine standalone master.

---

## 9. Companion work (separate doc)

The Destination master screen currently supports fewer types than the wizard — `toFormType`
(`destination-connection-dialog.component.ts:22-26`) maps only the SQL family and `Csv`/`Sftp`, so **MongoDB
destinations created in the wizard cannot be edited in Settings**. All 22 `DestinationType` values have working
writers and appear in the node library, but only 5 have any form.

Note for that plan: there are **no per-destination icons or colours to copy**. `RANK_META`
(`node-library-dialog.component.ts:92-103`) keys icon + colour by node *rank*, and every destination is rank 7 →
all 22 render as the same grey `▶` (`#64748B`). Only the `category` label differs (Relational / NoSQL / Analytics /
Cloud+FHIR / File / Delivery). Per-type icons and colours must be **designed once and shared** by the node
library and the master screen, or the two will drift.
