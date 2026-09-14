# 18 — Self-Contained Workflow Configuration Plan

Status: proposed (no code changes yet)

## 1. Problem

Workflow nodes store **pointers** to master records — `sourceConnectionId`, `destinationId`,
`mappingProfileIds` — and resolve them against master tables at run time. Transformation rules go
further: they live in a shared `TransformationRules` table and point *back* at the workflow via
`ResourcePipelineRouteId`.

That indirection is the root cause of a family of bugs:

1. **Editing a master silently changes every workflow using it.** No version bump, no audit trail on
   the workflow, no notification.
2. **Deletion requires guesswork.** `WorkflowConfigurationCleanupService` scans every other workflow
   to decide whether a mapping profile is safe to soft-delete.
3. **Cross-workflow bleed.** When a mapping profile id is missing, `TransformNodeExecutors` falls back
   to a lookup by `(resourceType, sourceConnectionId, destinationId)` — a triple shared by any workflow
   built on the same source/destination pair. One workflow silently uses another's mapping. Nothing
   errors; the data is just wrong.
4. **Rules authored before the workflow exists have no id.** They are written with
   `ResourcePipelineRouteId = null`, which `TransformationRule` itself documents as "inert" — the
   resolver matches on that id, so a null one can never fire. A save-time sweep
   (`AttachPendingRulesToWorkflowAsync`) repairs them by *guessing*: it filters orphans by destination
   type and stamps the saving workflow's id on whatever it finds. Two builder sessions on the same
   destination collide, and the loser's rules stay orphaned forever, because `AttachToWorkflow`
   refuses to re-point a bound rule.
5. **A save-time deadlock.** Mapping validation cannot see a rule the user can plainly see, because the
   rule cannot attach until the workflow saves and the workflow cannot save until validation passes.
   `includePendingWorkflowRules` exists solely to break this.
6. **Run history is not evidence.** `WorkflowDefinitions.Version` exists, but since config is by
   reference, re-running "version 3" can behave differently than the original.
7. **Workflows are not portable.** An exported workflow is a bag of ids meaningless in another database.

## 2. Decision

A workflow becomes a **self-contained document**. Every node carries the actual settings it needs.
Master records (source connections, destination configurations, mapping profiles, de-identification
profiles) become **authoring templates you copy from**, never records you point at.

Three decisions taken in review:

- **D1 — Snapshot + provenance.** Nodes store the real config plus metadata about where it was copied
  from, so the portal can offer "master has changed, pull update?" without the runtime ever depending
  on the master.
- **D2 — One transform tier.** Only ResourceType-level transformations and de-identification exist.
  The five-tier `TransformScope` chain (Global / DestinationType / ResourceType / Field / Workflow)
  collapses to rules held per-node.
- **D3 — Per-workflow copies of rules.** Transformation and de-identification rules live inside their
  node's configuration, not in a shared table.

### What is explicitly NOT changing

- Canvas rendering, node types, categories, positions, edges.
- `Rank` / `SubRank` ordering. This is a DAG, not a sequence — rank 10 can hold four parallel
  extractions. It is **not** being flattened to a single `SequenceNo`.
- The `WorkflowDefinitions` / `WorkflowNodes` / `WorkflowEdges` table structure.
- Master tables themselves. They remain, as templates.

## 3. Node configuration schema

`WorkflowNodes.ConfigurationJson` gains a consistent envelope. `config` is the only thing executors
read; `ref` is portal-only metadata.

### 3.1 Source node

```json
{
  "ref": { "masterId": "…", "masterVersion": 7, "masterName": "Epic Prod", "copiedOnUtc": "2026-09-14T…" },
  "config": {
    "sourceType": "Epic",
    "baseUrl": "https://fhir.example.org/api/FHIR/R4",
    "tokenEndpoint": "…",
    "clientId": "…",
    "applicationType": "BackendServices",
    "privateKeySecretRef": "kv://fhirbridge/src-epic-prod-a1b2c3",
    "ingestionMode": "Search",
    "resourceTypes": ["Patient", "Observation"]
  }
}
```

### 3.2 Destination node

```json
{
  "ref": { "masterId": "…", "masterVersion": 3, "masterName": "Analytics SQL", "copiedOnUtc": "…" },
  "config": {
    "destinationType": "SqlServer",
    "server": "…",
    "database": "…",
    "schema": "dbo",
    "credentialSecretRef": "kv://fhirbridge/dest-analytics-x7y8z9",
    "writeMode": "Upsert"
  }
}
```

### 3.3 Mapping node

```json
{
  "ref": { "masterId": "…", "masterVersion": 12, "masterName": "US Core Patient to dbo.Patient", "copiedOnUtc": "…" },
  "config": {
    "resources": [
      {
        "resourceType": "Patient",
        "destinationObject": "dbo.Patient",
        "fields": [
          { "sourceJsonPath": "$.id", "destinationField": "PatientId",
            "valueType": "String", "isUpsertKey": true },
          { "sourceJsonPath": "$.name[*].family", "destinationField": "LastName",
            "valueType": "String", "arrayPolicy": "First" }
        ]
      }
    ]
  }
}
```

### 3.4 Transformation node (D2 + D3)

Rules are inline. No `scope`, no `resourcePipelineRouteId`, no `destinationType` key — the node
already knows its workflow and its destination.

```json
{
  "config": {
    "rules": [
      {
        "resourceType": "Patient",
        "sourceField": "birthDate",
        "destinationField": "DOB",
        "nodeType": "DateTimeFormat",
        "order": 0,
        "onNull": "Skip",
        "errorPolicy": "NullOut",
        "arrayMode": "Whole",
        "expectedValueType": "Date",
        "config": { "format": "yyyy-MM-dd" }
      }
    ]
  }
}
```

### 3.5 De-identification node

Same shape, `executionPhase: "PreMapping"`, keyed by `sourceField` (raw FHIR path) rather than a
destination column. `deIdentificationProfileId` is replaced by `ref.masterId` — the profile becomes a
template copied from, matching every other node.

### 3.6 Secrets

Snapshots carry Key Vault **references**, never literal secret values. Unchanged from today.

## 4. Status model

Replaces the current Enabled/Disabled-only scheme.

| Status | Meaning | Runnable | Stored or derived |
|---|---|---|---|
| **Draft** | No destination node yet | No | Derived from graph |
| **Ready** | Source + destination present | Yes | Derived from graph |
| **Disabled** | Complete but deliberately paused | No | Stored (`IsEnabled = false`) |

- Draft/Ready are **computed**, never persisted — "has a destination node" is already knowable from
  the graph, and storing it lets it drift out of sync. `Disabled` stays a stored flag because it is a
  human decision, not a fact about the graph.
- A workflow is **fully editable in every status**. Reaching Ready locks nothing; adding transformation
  or de-identification later is the normal path.
- A Draft cannot run (nowhere to write) but **can** test its source connection via the existing
  node-checkpoint mechanism.
- "Submitted" was considered and rejected: it implies an approval step that does not exist.

### New-workflow flow

`New` on the workflow list opens a modal for **name + description**, saves immediately, and returns a
real `workflowId` before the canvas opens. This is what removes the "authored before the workflow
existed" class of bug at its root — there is no longer a window in which config has nowhere to live.

## 5. Deletion semantics

Node deleted → its configuration goes with it. Cascade already exists
(`SqlWorkflowDefinitionStore.DeleteAsync`); under snapshots it becomes *sufficient*, because there are
no longer orphaned master rows to chase.

Master records are **never** deleted by a workflow or node deletion. You copied from them; you do not
own them.

`WorkflowConfigurationCleanupService` is deleted entirely.

## 6. Code to delete

Each of these exists only to manage indirection or the null-id state.

### Backend

| Item | Location | Why it goes |
|---|---|---|
| `WorkflowConfigurationCleanupService` | `src/FHIRBridge.Application/Services/` | Nothing to orphan |
| `TransformScope` enum | `src/FHIRBridge.Domain/Enums/` | One tier (D2) |
| `EffectiveRuleResolver` 5-tier walk | `src/FHIRBridge.Application/Services/Transforms/` | Becomes filter + sort on node rules |
| `workflowScopedOnly` flag | same | No other tiers to fall through to |
| `includePendingWorkflowRules` flag | same | No pending state exists |
| `PreferFieldSpecific` / `PreferResourceTypeSpecific` / `PreferSourceSpecific` / `PreferSourceFieldSpecific` | same | Tie-breakers between tiers |
| `TransformationRule.AttachToWorkflow` | `src/FHIRBridge.Domain/Entities/` | No id to stamp |
| `AttachPendingRulesToWorkflowAsync` | `TransformationRuleService` | The guessing sweep |
| `FindByNaturalKeyAsync` | `TransformationRuleService` | 8-part composite key; a rule is now its list position |
| `GetPendingWorkflowRulesAsync`, `GetPendingWorkflowScopedAsync`, `GetWorkflowScopedAsync`, `GetFieldScopedAsync`, `GetDestinationTypeScopedAsync`, `GetGlobalScopedAsync` | `EfTransformationRuleRepository` | Tiers that no longer exist |
| `TransformationRules.ResourcePipelineRouteId` column | migration | Nothing to point at |
| ID-resolution + fallback chains | `TransformNodeExecutors`, `SourceNodeExecutors`, `DestinationNodeExecutors`, `WorkflowNodeExecutorBase` | Config is local |
| `SourceBuildSpec.ExistingId`, `DestinationBuildSpec.ExistingId`, `MappingBuildSpec.ExistingId` | `WorkflowBuildModels` | Copy, don't point |

### Decided — `WorkflowNodeConfigurations` table is dropped

Node config is stored **twice** today: `WorkflowNodes.ConfigurationJson` (blob) *and* the
`WorkflowNodeConfigurations` key/value table. `WorkflowNodeExecutorBase` has to reconcile them because
structured values ("fields", "mappingProfileIds") do not fit a key/value shape.

**Decision: collapse to JSON-only.** This matches §2.c as originally specified — one configuration
store per node, holding JSON. Dropped:

| Item | Location |
|---|---|
| `WorkflowNodeConfiguration` entity | `src/Runtime/FHIRBridge.Runtime.Domain/Workflows/` |
| `WorkflowNodeConfigurationEntityTypeConfiguration` | `WorkflowPersistenceConfigurations.cs` |
| `WorkflowNodeConfigurations` table | migration |
| `WorkflowNode.AddConfiguration`, `WorkflowNode.Configuration` | `WorkflowNode.cs` |
| `WorkflowDefinition.AddNodeConfiguration` | `WorkflowDefinition.cs` |
| Key/value reconciliation in `ReadConfiguration` / `ReadStringConfiguration` | `WorkflowNodeExecutorBase.cs` |
| `.ThenInclude(node => node.Configuration)` loads | `SqlWorkflowDefinitionStore.cs` |

Executors that read a key/value entry today (e.g. `DestinationNodeExecutors.cs:1040` reading
`destinationId`) read the equivalent path out of `ConfigurationJson` instead. Migration folds any
key/value rows into their node's JSON before the table is dropped.

One flag: `WorkflowNodeConfiguration.IsSecret` has no equivalent in a plain JSON blob. Secrets are
already stored as Key Vault *references* (§3.6), not values, so nothing secret is actually protected by
that column today — but if any row has `IsSecret = true` with a literal value, migration must report it
rather than silently inline it.

### Portal

| Item | Location |
|---|---|
| ID-stamping pass | `workflow-graph-mapper-v2.service.ts:167-197` (and v1 equivalent) |
| `existingId` emission | `workflow-build-assembler-v2.service.ts:148,168` |
| Legacy `mappingProfileId` fallbacks | `workflow-build-assembler-v2.service.ts:1100-1110` |

## 7. Data migration

One migration, forward-only, per node:

1. Read the node's `sourceConnectionId` / `destinationId` / `mappingProfileIds`.
2. Resolve each against its master **once**.
3. Write the resolved settings into `config`, and the originating id/version into `ref`.
4. For every `TransformationRules` row with a non-null `ResourcePipelineRouteId`: fold it into the
   owning workflow's transform node `config.rules`.
5. Orphan rules (`ResourcePipelineRouteId IS NULL`) are **reported, not migrated** — they belong to no
   workflow and are inert today. Operator decides.
6. Drop `ResourcePipelineRouteId`.

Non-negotiable: a **dry-run mode** that reports what each node would resolve to, and a pre-flight that
fails loudly on any node whose ids do not resolve. A node pointing at a deleted master is exactly the
corruption this work exists to prevent — it must not be silently written as an empty snapshot.

## 8. Builder v1 retirement — decided

V1 is deleted entirely, and v2 is renamed to become the only builder.

### 8.1 Delete

| Item | Path |
|---|---|
| Route | `app.routes.ts` — the `workflow-builder` entry (~lines 59-71) |
| Page | `portal/src/app/pages/workflow-builder/` |
| Node library | `portal/src/app/components/node-library/` (incl. its `destination-wizard/` + `destination-forms/`) |
| Assembler | `portal/src/app/services/workflow-build-assembler.service.ts` |
| Graph mapper | `portal/src/app/services/workflow-graph-mapper.service.ts` |
| Any v1-only store/model/spec files those pull in | follow imports |

### 8.2 Rename (v2 becomes the builder)

`workflow-builder-v2` → `workflow-builder`, `node-library-v2` → `node-library`,
`*-v2.service.ts` → `*.service.ts`, and the corresponding class names
(`WorkflowBuildAssemblerServiceV2` → `WorkflowBuildAssemblerService`, etc.). The `-v2` suffix stops
distinguishing anything the moment v1 is gone; keeping it is permanent cosmetic debt.

Do the delete and the rename as **two separate commits** — a rename touching every import is far easier
to review when it is not mixed with deletions.

### 8.3 Backend follow-on

`NormalizationNode` exists specifically so that v1 pipelines cannot run field-level transform rules —
`WorkflowNodeTypes.FhirResourceTransform` documents this as a deliberate split. Once §8.4 has converted
every stored graph, `NormalizationNode` and `NormalizationNodeExecutor` are dead and removed. Verify no
stored graph still references it before deleting.

### 8.4 V1 graph migration

Existing v1-built workflows are **converted**, not abandoned. The §7 migration gains a second pass:

1. Detect v1 graphs by their v1-only node types (`NormalizationNode`).
2. Rewrite `NormalizationNode` → `FhirResourceTransformNode` where the node's position in the graph
   makes it a transform step.
3. Re-rank if the v1 and v2 catalogs disagree on rank for any converted type.
4. Re-point edges — node ids are preserved, so edges survive untouched.

Same dry-run requirement as §7: report every graph that would be converted, and what each node becomes,
before writing anything. A v1 graph that cannot be cleanly converted is **reported and left alone**, not
half-migrated.

### 8.5 Ordering constraint

§8.4 must run **before** §8.1, or v1 workflows become unopenable with no builder able to read them.
Sequence: convert graphs → verify every workflow opens in v2 → then delete v1.

## 9. Save-button consolidation — DEFERRED

**Status: parked.** Not part of the current scope. Revisit after the snapshot model (§3, §7) and v1
retirement (§8) have landed — the prerequisites in §9.3 are exactly that work, so this section is
unblocked by it rather than competing with it.

Kept here in full so the analysis is not re-derived later.

### 9.1 The problem

Configuring one destination currently asks for **three separate saves**:

1. Step 2 of the destination wizard, where resource types are picked.
2. The Map screen (in-canvas header Save — `buildAndShowMappingSummary()`).
3. The transform-rule configuration screen.

Each writes to a different place at a different time, which is precisely the indirection this plan
removes. The three saves are not a UI accident — they exist *because* connections, mapping profiles and
rules are separate master records that must be persisted independently before anything can reference
them by id.

The clearest symptom is the blocked state in `destination-wizard.component.html:1262`:

> "Save this workflow first, then reopen the mapping to override or bypass a conflicting rule for it."

The user is told to leave, save, and come back — because a rule override needs a workflow id that does
not exist yet.

### 9.2 Target

**One Save**, on the Mapping screen, committing the whole destination configuration — connection,
resource selection, mapping, transformation rules, de-identification — as a single unit.

Steps 1 and 2 keep `Back` / `Next` for navigation but **persist nothing**. Their state is held in the
builder until the single Save.

### 9.3 Why this becomes possible

It is a direct consequence of the earlier decisions, not independent UI work:

- **§2 D1/D3** — config and rules live in the node, so there is nothing to persist separately and
  nothing needing an id up front.
- **§4 new-workflow modal** — the workflow id exists before the canvas opens, so the
  "save the workflow first" dead-end disappears entirely.

Attempting this consolidation *without* those changes would not work: the intermediate saves exist to
create the ids the later steps depend on.

### 9.4 Scope

| Change | Where |
|---|---|
| Remove Step 2 save; hold selection in builder state | `destination-wizard.component.ts` |
| Remove standalone rule-config save; fold into node config | transform rule config screen |
| Single Save commits connection + mapping + rules + de-id | `saveChainNode()` / `next()` at `TOTAL_STEPS` |
| Delete the "save this workflow first" blocked state | `destination-wizard.component.html:1262` |
| Rule conflicts resolve inline (id always exists now) | `pendingRuleConflicts` handling |

`pendingSaveWarnings` (the "save with N warnings?" confirm) is **kept** — that is a genuine
confirmation of a risky action, not a redundant save step.

### 9.5 Constraint

Consolidating to one Save means more unsaved work in flight, so the builder must warn on navigate-away
with unsaved node config. Today an accidental close loses less because Step 2 was already persisted.
This is a real regression risk if skipped — call it out in the phase, do not leave it to be noticed
later.

## 10. Sequencing

| Phase | Work | Depends on |
|---|---|---|
| 1 | Status model + new-workflow name/description modal (§4) | — |
| 2 | Node config envelope `ref` + `config` (§3); readers accept old **and** new shapes | — |
| 3 | Migration + dry-run: ids → snapshots, rules inlined, key/value folded in (§7) | 2 |
| 4 | V1 graph conversion + dry-run (§8.4); verify every workflow opens in v2 | 3 |
| 5 | Executors read snapshots only; delete resolution/fallback chains (§6) | 3 |
| 6 | Rules inline; collapse `TransformScope`; delete resolver tiers (§6) | 5 |
| 7 | Portal: assemblers/mappers emit snapshots; "master changed" affordance | 5 |
| 8 | Delete v1; then rename v2 → builder — **two commits** (§8.1–8.2) | 4, 7 |
| 9 | Drop `ResourcePipelineRouteId` + `WorkflowNodeConfigurations`; remove `NormalizationNode` | 6, 8 |
| — | *Deferred:* single Save consolidation (§9) | after 8 |

Two ordering constraints, each a way to break production if ignored:

- **Phase 2 dual-read is what makes this safe.** Old and new node shapes coexist until the migration is
  verified — no flag day.
- **Phase 4 before phase 8.** Convert v1 graphs while v1 still exists; deleting the builder first leaves
  those workflows unopenable (§8.5).

Phase 1 is independent of everything else and can land immediately — it is also the phase that removes
the null-id bug at its root.

## 11. Accepted trade-off

**Masters stop propagating automatically.** Fixing a wrong URL in "Epic Prod" no longer takes effect
everywhere on its own; each workflow must pull the update. Mitigated by the `ref` metadata driving a
"3 workflows are behind this master — update them?" action, making it a click rather than a hunt.

This cost was reviewed and accepted: with no global transform tier (D2), shared defaults were not being
used in the first place.
