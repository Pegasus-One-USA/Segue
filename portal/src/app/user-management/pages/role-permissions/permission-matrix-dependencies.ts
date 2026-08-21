// user-management/pages/role-permissions/permission-matrix-dependencies.ts
//
// Role Permissions UI consistency rule (NOT a backend authorization change — see
// WorkflowModuleAccessAuthorizationHandler and the module doc comment in permission-matrix.config.ts
// for why node permissions and Workflow module permissions are deliberately independent at the
// AUTHORIZATION layer). This file only governs what gets displayed/saved on THIS screen, so that
// granting a node action never produces a role whose saved permission set looks incomplete on its
// own: checking `epic.edit` also checks `workflow.edit` + `workflow.view`, because editing an Epic
// node inside a workflow is meaningless without also being able to reach/edit the workflow itself.
//
// Two-layer model:
//   - EXPLICIT codes — what the admin has actually, directly toggled on that exact row/action (or
//     what was loaded from the database — see role-permissions.component.ts's loadData/save, which
//     treats a freshly (re)loaded role's stored permissions as the new explicit baseline, since the
//     database has no column to record "this one was only ever implied").
//   - EFFECTIVE codes — explicit ∪ the transitive closure of required parents over explicit. This is
//     what's actually displayed as checked and what gets persisted on Save (computeEffectiveCodes).
// Keeping these separate is what lets "Epic → Edit" and "SQL Server → Edit" both independently keep
// Workflow → Edit alive while either one is still explicit-or-implying-it, while still letting
// Workflow → Edit disappear once NEITHER needs it anymore (unless an admin explicitly granted it in
// its own right) — see role-permissions.component.ts's togglePermission/toggleRow/toggleAll.
//
// Derived generically from MATRIX_SECTIONS's own 'workflow-nodes' section — adding a new vendor row
// there (e.g. a future NewEHR) makes it participate in this dependency system automatically, with no
// change needed here.

import { MATRIX_SECTIONS } from './permission-matrix.config';

/** Every vendor/destination-type prefix the Workflow Nodes table manages (e.g. 'epic', 'sqlserver')
 *  — read straight off the matrix config's own rows/actions, never hand-maintained here. */
const NODE_PREFIXES: ReadonlySet<string> = new Set(
  (MATRIX_SECTIONS.find(s => s.id === 'workflow-nodes')?.rows ?? [])
    .flatMap(row => row.actions ?? [])
    .map(action => action.code.split('.')[0])
);

/** A node action's name doesn't always match the Workflow row's own action name for the same
 *  concept — Workflow's fifth action is `workflow.run` (labeled "Execute" in the UI, per
 *  permission-matrix.config.ts), while every node's own fifth action is literally `{prefix}.execute`.
 *  Every other action name matches verbatim. */
const WORKFLOW_ACTION_FOR_NODE_ACTION: Readonly<Record<string, string>> = {
  view: 'view',
  create: 'create',
  edit: 'edit',
  delete: 'delete',
  execute: 'run',
};

/** The Workflow-module codes `code` requires to be logically consistent, per the table:
 *    node.view    → workflow.view
 *    node.create  → workflow.create + workflow.view
 *    node.edit    → workflow.edit   + workflow.view
 *    node.delete  → workflow.delete + workflow.view
 *    node.execute → workflow.run    + workflow.view
 *  Returns [] for any code that isn't one of the Workflow Nodes table's own actions (role.*, user.*,
 *  mappingprofiles.*, the workflow.* codes themselves, ...) — those have no such relationship. */
export function requiredParentCodes(code: string): readonly string[] {
  const dot = code.indexOf('.');
  if (dot < 0) return [];
  const prefix = code.slice(0, dot);
  const action = code.slice(dot + 1);
  if (!NODE_PREFIXES.has(prefix)) return [];

  const workflowAction = WORKFLOW_ACTION_FOR_NODE_ACTION[action];
  if (!workflowAction) return [];

  const workflowCode = `workflow.${workflowAction}`;
  return workflowAction === 'view' ? [workflowCode] : [workflowCode, 'workflow.view'];
}

/** Explicit ∪ transitive closure of required parents — the displayed/saved set. Recursive rather
 *  than a fixed two-level walk so a future third layer (if one's ever added) resolves correctly
 *  without changing this function. */
export function computeEffectiveCodes(explicit: ReadonlySet<string>): Set<string> {
  const effective = new Set<string>();
  for (const code of explicit) {
    addWithParents(code, effective);
  }
  return effective;
}

function addWithParents(code: string, set: Set<string>): void {
  if (set.has(code)) return;
  set.add(code);
  for (const parent of requiredParentCodes(code)) {
    addWithParents(parent, set);
  }
}

/**
 * Applies one checkbox change to the EXPLICIT set (not the effective/displayed one) and returns the
 * new explicit set — pure, no mutation of the input.
 *
 * - Checking a code adds it to explicit. Nothing else — its required parents are never added to
 *   explicit; they only ever appear via computeEffectiveCodes's closure, which is exactly what lets
 *   them disappear later if nothing still needs them.
 * - Unchecking a code removes it from explicit AND cascades to remove every OTHER explicit code that
 *   requires it — this is necessary even when `code` itself was never in explicit (e.g. the admin
 *   unchecks "Workflow → Edit" while it was only ever showing as checked because `epic.edit` implied
 *   it): without the cascade, epic.edit's own closure would silently re-imply workflow.edit right
 *   back into the effective set on the very next read, making the click a visible no-op.
 */
export function toggleExplicitCode(
  explicit: ReadonlySet<string>,
  code: string,
  checked: boolean,
  allCodes: readonly string[],
): Set<string> {
  const next = new Set(explicit);
  if (checked) {
    next.add(code);
  } else {
    removeExplicitAndDependents(code, next, allCodes);
  }
  return next;
}

function removeExplicitAndDependents(code: string, explicit: Set<string>, allCodes: readonly string[]): void {
  // Deliberately unconditional (no "already absent, nothing to do" early return) — `code` may be
  // displayed as checked purely via implication without ever having been in `explicit` itself; the
  // cascade below still needs to run in that case. Safe from infinite recursion / reprocessing: the
  // dependency graph is a strict two-layer DAG (node codes require workflow codes; workflow codes
  // require nothing), so recursing into a node-code dependent always terminates one level deeper with
  // nothing left to cascade, and the `explicit.has(dependent)` check below prevents ever revisiting a
  // code this same call has already removed.
  explicit.delete(code);
  for (const dependent of allCodes) {
    if (explicit.has(dependent) && requiredParentCodes(dependent).includes(code)) {
      removeExplicitAndDependents(dependent, explicit, allCodes);
    }
  }
}
