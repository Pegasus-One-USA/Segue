# Workflow V3 — workflow-scoped transformation rules

Branch: `feature/workflowv3` · Worktree: `../FHIRBridge-workflowv3` · Forked from `main_v2` @ `f00ff8a6`

## Why this branch exists

Transformation rules are persisted **the moment they are authored** (`TransformationRulesService.save`
fires an HTTP POST per click), but a workflow only gets an id **when it is saved**. Rules authored before
that first save were written with `ResourcePipelineRouteId = NULL`.

`NULL` is not inert. `GetPendingWorkflowRulesAsync` matches on it, so a null-route rule reads as
*"belongs to whoever asks"* — one workflow's draft rules surface in every other workflow.

Everything below follows from closing that gap: **the workflow id must exist before the first rule is
written.**

## The five requirements (user's words)

1. Create Source
2. Create Destination, Mapping, Transformation (if want to add)
3. Add (if not added in destination) or Edit (if added in destination) Transformation Node and add or edit
   rules **irrespective of whether node type is repeated or not**, **irrespective of whether workflow is
   saved or not**
4. There will not be a concept of global rules — it will always be a workflow rule, so remove any global
   rule logic
5. If rule(s) exist, the Transformation node should be visible

De-identification was explicitly dropped from scope.

## Root causes found (verified in code, 2026-09-11)

| # | Symptom | Root cause |
|---|---|---|
| 1 | Same node type on a second column — only one survives on create, edit works | `TransformationRuleService.FindByNaturalKeyAsync` **guesses** create-vs-update when the portal sends no `id`. Its lookup goes through `ListAsync`, where a null argument means *match anything* — with no `destinationType`/`resourcePipelineRouteId` those filters vanish, the other column's rule matches, and the save becomes `existing.Update(...)`, overwriting instead of inserting. Edit mode sends a real id, so `GetByIdAsync` short-circuits the guess. |
| 2 | Rules exist but the Transformation node is invisible | **Scope mismatch.** The rules dialog saves `scope: 'Field'` (`transform-rules-dialog.component.ts:408`); the reader asks with `workflowScopedOnly: true` (`field-mapping-list.component.ts:190`), which consults only the Workflow tier and returns `[]`. A Field-scoped rule can never appear there. |
| 3 | Added via Mapping-node `+` → **is** visible | Not a bug — the control case. That path saves `scope: 'Workflow'` **with** a route id (`field-mapping-join-popover.component.ts:151`), landing in the one tier the tab queries. |
| 4 | Rules appear that were never added to this workflow | (a) `GetPendingWorkflowScopedAsync` matches `ResourcePipelineRouteId IS NULL` with **no workflow filter** — every unattached draft rule for that destination type leaks into every workflow. (b) Field-scoped rules match by resource + column name regardless of workflow. |

Key architectural fact: `EffectiveRuleResolver` walks five tiers (`Workflow 4 → Field 3 → ResourceType 2 →
DestinationType 1 → Global 0`) and **stops at the first tier with a match**. Requirement #4 deletes tiers
0–3 for PostMapping.

## Design chosen

Options weighed: **A** client-minted GUID · **B** defer all rule writes to canvas state · **C** hidden draft
row on node drop · **D** client GUID + always-send-id.

**Chosen (user's own design):** create the workflow **up front** from a Name/Description modal, then persist
each piece against that id as it is added.

```
New Workflow → modal (name, description) → POST /workflows → id a3f9…  [status: Draft]
Add source    → "Add to Workflow" persists source connection + node    → a3f9…
Add dest      → persists destination + node; leaves Draft              → a3f9…
Add rule      → written with RouteId = a3f9… (correct from the first)  → a3f9…
Save          → name/description edits, node deletions, edges, trigger
```

Why this over the alternatives: the id is server-minted on a real save (no client GUID), and an abandoned
build leaves a **coherent, visible, deletable** workflow rather than orphan rule rows (A/D) or a workflow
whose rules reference a source and destination that were never saved (modal-only).

Decisions confirmed with the user:
- Each "Add to Workflow" **does** persist the node.
- Final Save handles the delta: name, description, node deletions, edges, trigger.
- Empty workflows list as **Draft** until a destination is added.

## Status

### Step 1 — New Workflow modal — DONE (builds + tests clean)

"New Workflow" opens a modal for Name + Description. Create persists the workflow and lands the builder in
edit mode with a real id. URL is rewritten to `?id=<new-id>` so a refresh reloads rather than orphaning it.

| File | Change |
|---|---|
| `portal/.../workflow-builder-v2.component.ts` | Modal signals, `createNewWorkflow()`, `cancelNewWorkflow()`; modal opens when `ngOnInit` sees no `?id=` |
| `portal/.../workflow-builder-v2.component.html` | Modal markup, reusing `.reset-backdrop` / `.reset-dialog` |
| `portal/.../workflow-builder-v2.component.scss` | `.nw-*` field styles; `:has(.nw-input)` re-aligns the shared dialog left |
| `src/Api/.../WorkflowEndpoints.cs` | Summary status derives `"Draft"` when `!hasDestination` |

Three things that made this smaller than planned:
- **`POST /workflows` already existed** (`WorkflowEndpoints.cs:61`) — pure create, mints the id. The portal's
  `workflowApi.save(request)` with no id already calls it. No new endpoint, no new service method.
- **Nothing requires a workflow to have nodes** — an empty workflow is already valid.
- **Status was already a derived string** with `hasDestination` computed beside it → Draft is one line, **no
  EF migration**, nothing to roll back.

`cancelNewWorkflow()` navigates to `/workflows` rather than leaving the user on a canvas with no workflow
behind it, since every later action assumes an id exists.

### Step 2 — "Add to Workflow" persists the node — NOT STARTED

Needs incremental node endpoints (`POST/PUT/DELETE /workflows/{id}/nodes/...`). Today the only save path is
`POST /workflows/build` with the **whole graph**, and it validates a *complete* workflow — a source-only
graph would fail that. Must handle version bump (`WorkflowDefinition.Version`), `RowVersion` concurrency,
and validation of a legitimately incomplete mid-build graph.

### Step 3 — Delete the tier system — NOT STARTED (fixes #4, and #2/#5 with step 4)

- `IEffectiveRuleResolver` — drop the Field/ResourceType/DestinationType/Global PostMapping tiers
- `EfTransformationRuleRepository` — drop `GetFieldScopedAsync`, `GetResourceTypeScopedAsync`,
  `GetDestinationTypeScopedAsync`, `GetGlobalScopedAsync`, `GetPendingWorkflowRulesAsync`,
  `GetPendingWorkflowScopedAsync`
- `TransformationRulesController` — drop `POST attach-pending/{workflowId}`
- `TransformScope` — collapses toward a single value (~120 references across 24 files, mostly tests that
  disappear with the tiers)
- Portal — drop `includePending`, delete `attachPendingRules` from the builder

**Do not touch PreMapping.** De-identification resolves by *profile* (`GetPreMappingRulesAsync`), a separate
path. It is out of scope and must keep working.

### Step 4 — Tighten the rule natural key — NOT STARTED (fixes #1)

Add `resourcePipelineRouteId` + `destinationType` to `FindByNaturalKeyAsync`, and have the dialogs always
send a real `id` on edit so the guess is never reached. Switch `transform-rules-dialog.component.ts` from
`scope: 'Field'` to `scope: 'Workflow'` + route id (fixes #2).

### Step 5 — Data cleanup — NOT STARTED

The 2 `ResourceType`/`FhirResource` rules become unreachable when the tiers go. Back up
(`pg_dump --column-inserts`) and drop, same as the 15 below.

## Database state

Control plane is **PostgreSQL**, port **5434**, container `fhirbridge-controlplane-pg`, db `FHIRBridge`.

Already done this session, in the user's DB (**not** on this branch — the DB is shared):

- **Deleted 15 `Field`-scoped rules.** All user test data from 03–07 Sep, all Patient/SqlServer. Included
  **6 byte-identical `BirthDateAge`/DateMathAge duplicates** (bug #1 visible in the data) and a 3-way
  conflict on `Identifier` (two `ReferenceConstruction` reading different source fields + one
  `IdentifierFormatting`, all writing the same column).
  Backup: `<scratchpad>/field-scope-rules-backup.sql` — restore with `psql -f`.

Remaining:

| Scope | Phase | Count | Note |
|---|---|---|---|
| Workflow | PostMapping | 20 | 9 attached, **11 unattached** — these are the #4 leak |
| ResourceType | PreMapping | 4 | Safe Harbor de-identification — **out of scope, leave alone** |
| ResourceType | FhirResource | 2 | Becomes unreachable at step 3 — back up and drop |

## How to resume

```bash
cd "C:/Me/PegasusOne/1. Projects/FHIRBridge-workflowv3"
git status                  # clean except intended changes
dotnet build src/Api/FHIRBridge.Api
cd portal && npx ng build --configuration development
```

The worktree has its **own build output**, so it builds and tests fully even while the API runs from the
main tree. That was the reason for using a worktree rather than a branch switch.

## Test baseline (pre-existing, not caused by this work)

- `FHIRBridge.UnitTests` — **2 failed / 1177 passed**: `DestinationExecutionHistoryGateTests.Update_throws_when_destination_has_execution_history`,
  `ForgotPasswordResetPasswordTests.ForgotPasswordAsync_existing_local_user_returns_Accepted_and_persists_a_hashed_token`
- `FHIRBridge.ArchitectureTests` — **1 failed / 5 passed**: `ApplicationTypeDispatchTests.No_engine_code_switches_on_ApplicationType`,
  sole offender `WorkflowEndpoints.cs`
- `FHIRBridge.Runtime.UnitTests` — 2 failed / 283 passed

Anything beyond these is new. Re-verify in a clean worktree at HEAD before blaming a change.

## Gotchas

- **CRLF everywhere** (`.editorconfig`). Scripted edits must round-trip line endings, and must not add a
  BOM to a file that had none — both produce phantom whole-file diffs.
- **Do not stop the user's running API.** It holds the main tree's build output; that is what the worktree
  exists to work around.
- `ResourcePipelineRouteId` on a rule stores the **`WorkflowDefinition` id**. The `ResourcePipelineRoutes`
  table is empty/unused.
- The runtime reads `resourcePipelineRouteId` from the **Mapping node's own `ConfigurationJson`**, not from
  the rule.
- `TransformErrorPolicy.NullOut` means a *failed* rule writes NULL. Receiving the **raw source value**
  therefore proves **no rule ran at all**, not that one failed. Useful diagnostic lever.
- `ExpectedValueType` gates `CreateMappingProfileRequestValidator`'s column-type check. A rule that reshapes
  its value (DateMathAge Date→Integer) must declare it, or the save is rejected with
  *"'Age' is a int column (expects Integer), but this field is mapped as Date."*
- v1 (`node-library`) and v2 (`node-library-v2`) portal trees are near-identical and were historically
  edited in pairs. Steps 3–4 touch v2; confirm whether v1 still needs the same edit.
