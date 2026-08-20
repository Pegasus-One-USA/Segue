// user-management/pages/role-permissions/permission-matrix.config.ts
//
// Presentation layout for the Role Permissions screen — a flat row/column matrix, grouped into the
// same sections as the app's real menus (Access Control, Workflows, Settings, Logs & Compliance)
// but with NO tree nesting and NO cross-row dependencies: every checkbox here controls exactly one
// existing permission code, independently. Introduces no new permission, group, or backend concept
// — every `code` below is a wire code (`group.action`) that already exists in the catalog returned
// by GET /api/v1/permissions/catalog.
//
// Deliberately has no "requires" concept at all (unlike the hierarchical tree UI this replaces):
// inspection of the backend (WorkflowEndpoints.cs's per-vendor checks, the terminology
// controllers, etc.) confirms none of these permissions actually depend on one another server-side
// — epic.edit and workflow.view are checked as two unrelated permissions, not a dependent pair.
// The old auto-check/cascade behavior was a frontend-only convenience, not a security rule, so
// removing it doesn't loosen anything the backend enforces.
//
// ── Canonical-Node-Catalog-driven Workflow Nodes ────────────────────────────────────────────────
// Every other section below is hand-authored and stays that way on purpose (see the module doc
// comment further down for why — several of these rows encode a real editorial decision, like
// splitting one backend permission group across two UI rows, that no generic rule could safely
// reconstruct). The Workflow Nodes section is different: every row there maps 1:1 onto exactly one
// backend PermissionGroupCode, with actions that are exactly that group's own permissions — nothing
// hand-picked, nothing split. So its ROWS and COLUMNS are both built at runtime from the canonical
// Node Catalog (GET /api/v1/permissions/node-catalog — see buildMatrixSections below), the SAME
// catalog the Workflow Builder Node Library reads from, instead of being listed here or derived
// separately from the generic /permissions/catalog response. Adding `epic.archive` (a new action)
// makes an ARCHIVE column appear on the Epic row automatically. Adding a new PermissionGroupCode
// member for a brand-new vendor makes an entire new row appear automatically. Neither requires
// touching this file. Legacy `.read`/`.assign` actions are already excluded server-side (see
// NodeCatalogBuilder), so this file no longer needs its own exclusion list.

import { NodeCatalogEntry } from '../../../models/node-catalog.model';

export interface MatrixAction {
  /** Column header this action renders under — shared across every row in the same section, so
   *  this is always the action's real, undisguised name (e.g. 'Write', never a per-row nickname
   *  like 'Import' or 'Edit' for the same underlying code). */
  label: string;
  code: string;
  /** Overrides the checkbox's tooltip (normally the live catalog's own `perm.description`) — for a
   *  code whose real backend meaning needs more disambiguation than its description already gives
   *  in THIS specific table's context. Currently only used for the Workflow Nodes section's Delete
   *  column — see the section-level `note` on that section for why. */
  tooltipOverride?: string;
}

export interface MatrixRow {
  id: string;
  label: string;
  /** This row's own actions. Absent/empty only for a superAdminOnly row (no permission exists). */
  actions?: MatrixAction[];
  /** Visible but not selectable — no permission exists or ever should for this row; only a
   *  SuperAdmin (role membership, not a permission grant) can reach it. */
  superAdminOnly?: boolean;
  /** Short explanatory caption, e.g. for a row that shares another row's exact code. */
  note?: string;
}

export interface MatrixSection {
  id: string;
  label: string;
  rows: MatrixRow[];
  /** Persistent, always-visible clarification rendered under this section's header — for a section
   *  where a column's plain name (e.g. "Delete") could otherwise be misread in a way that matters.
   *  Currently only set on `workflow-nodes` — see that section's own comment for the exact ambiguity. */
  note?: string;
}

// Every source vendor / destination type node has its own full set of independent actions — no
// dependency between them (checking View does not imply Create, etc.), matching the same actions
// the Workflow row itself exposes above. Only used for the two hand-authored sections below that
// still enumerate their own actions literally (Access Control, Settings) — the Workflow Nodes
// section itself no longer uses this; see buildWorkflowNodesSection.
const terminologySystem = (id: string, label: string, codePrefix: string): MatrixRow => ({
  id, label,
  actions: [
    { label: 'View', code: `${codePrefix}.view` },
    { label: 'Write', code: `${codePrefix}.write` },
  ],
});

// Every section except Workflow Nodes — real editorial layout that a generic rule can't safely
// reconstruct from the catalog (e.g. Branding/Email below deliberately expose two *different*
// subsets of the same Configuration group's actions; Execution History deliberately aliases
// Workflow's own View permission under a second row/label). Preserved exactly as before.
const STATIC_SECTIONS: MatrixSection[] = [
  {
    id: 'access-control',
    label: 'Access Control',
    rows: [
      {
        id: 'role', label: 'Role',
        actions: [
          { label: 'View', code: 'role.view' },
          { label: 'Create', code: 'role.create' },
          { label: 'Edit', code: 'role.edit' },
          { label: 'Delete', code: 'role.delete' },
          // Backend group for this permission is Role (RbacSeedData: "Assign or remove roles from
          // users."), not User — placed on this row to match the real permission it grants, not
          // where it conceptually reads best.
          { label: 'Assign', code: 'role.assign' },
        ],
      },
      {
        id: 'user', label: 'User',
        actions: [
          { label: 'View', code: 'user.view' },
          { label: 'Invite', code: 'user.invite' },
          { label: 'Edit', code: 'user.edit' },
          { label: 'Deactivate', code: 'user.deactivate' },
        ],
      },
    ],
  },
  // Workflow is the parent/module — kept as its own section, deliberately separate from the node
  // table below. It is NOT another selectable "node" alongside Epic/SQL Server/etc.: a node
  // permission implies module ACCESS (see PermissionService.hasWorkflowModuleAccess /
  // WorkflowModuleAccessAuthorizationHandler), but never the reverse, and never any of the CRUD
  // actions below — those stay gated on their own literal code, independent of every node row.
  {
    id: 'workflow-module',
    label: 'Workflow',
    rows: [
      // Dashboard is intentionally NOT a row here (or anywhere in this matrix) — it's a mandatory
      // platform entry point every authenticated role can reach, not an RBAC-controlled permission.
      // See sidebar.component.ts/app.routes.ts's dashboard entries for where that's enforced.
      {
        id: 'workflow', label: 'Workflow',
        actions: [
          { label: 'View', code: 'workflow.view' },
          { label: 'Create', code: 'workflow.create' },
          { label: 'Edit', code: 'workflow.edit' },
          { label: 'Delete', code: 'workflow.delete' },
          { label: 'Execute', code: 'workflow.run' },
        ],
      },
      { id: 'execution-history', label: 'Execution History', note: "Uses Workflow's View permission", actions: [{ label: 'View', code: 'workflow.view' }] },
    ],
  },
  // 'workflow-nodes' is inserted here at runtime by buildMatrixSections — see this file's module
  // doc comment for why it's catalog-driven instead of listed alongside these sections.
  {
    id: 'settings',
    label: 'Settings',
    rows: [
      { id: 'branding', label: 'Branding', actions: [{ label: 'Write', code: 'configuration.write' }] },
      {
        id: 'source-connections', label: 'Source Connections',
        actions: [
          { label: 'View', code: 'sourceconnections.view' },
          { label: 'Create', code: 'sourceconnections.create' },
          { label: 'Edit', code: 'sourceconnections.edit' },
          { label: 'Delete', code: 'sourceconnections.delete' },
          { label: 'Test', code: 'sourceconnections.test' },
        ],
      },
      {
        id: 'destination-connections', label: 'Destination Connections',
        actions: [
          { label: 'View', code: 'destinationconnections.view' },
          { label: 'Deactivate', code: 'destinationconnections.deactivate' },
          { label: 'Delete', code: 'destinationconnections.delete' },
        ],
      },
      {
        id: 'mapping-profiles', label: 'Mapping Profiles',
        actions: [
          { label: 'View', code: 'mappingprofiles.view' },
          { label: 'Create', code: 'mappingprofiles.create' },
          { label: 'Edit', code: 'mappingprofiles.edit' },
          { label: 'Deactivate', code: 'mappingprofiles.deactivate' },
          { label: 'Delete', code: 'mappingprofiles.delete' },
        ],
      },
      {
        id: 'transformation-rules', label: 'Transformation Rules',
        actions: [
          { label: 'View', code: 'transformationrules.view' },
          { label: 'Write', code: 'transformationrules.write' },
          { label: 'Delete', code: 'transformationrules.delete' },
        ],
      },
      {
        id: 'ehr-endpoints', label: 'EHR Endpoints',
        actions: [
          { label: 'View', code: 'ehrendpoints.view' },
          { label: 'Create', code: 'ehrendpoints.create' },
          { label: 'Edit', code: 'ehrendpoints.edit' },
          { label: 'Delete', code: 'ehrendpoints.delete' },
        ],
      },
      { id: 'allowed-origins', label: 'Allowed Origins', superAdminOnly: true },
      { id: 'email', label: 'Email', actions: [{ label: 'View', code: 'configuration.view' }, { label: 'Write', code: 'configuration.write' }] },
      terminologySystem('loinc', 'LOINC', 'loinc'),
      terminologySystem('snomed-ct', 'SNOMED CT', 'snomedct'),
      terminologySystem('rxnorm', 'RxNorm', 'rxnorm'),
      terminologySystem('icd-10', 'ICD-10', 'icd10'),
      { id: 'general', label: 'General', superAdminOnly: true },
      { id: 'security', label: 'Security', superAdminOnly: true },
    ],
  },
  {
    id: 'logs-compliance',
    label: 'Logs & Compliance',
    rows: [
      { id: 'logs-compliance', label: 'Logs & Compliance', actions: [{ label: 'Read', code: 'governance.read' }] },
    ],
  },
];

// Delete's tooltip means something more specific than its catalog description for every node row —
// see the section-level `note` below for the full explanation. The only per-action override this
// screen needs; every other action falls back to the catalog's own permission description exactly
// as before (see role-permissions.component.html's cell.tooltipOverride ?? cell.perm.description).
const NODE_DELETE_TOOLTIP_OVERRIDE =
  'Allows removing this node type from a workflow. Also required (alongside Source/Destination Connections → Delete) to delete this vendor’s stored connection in Settings.';

// Preserves today's curated row order (Epic first, then the other EHR vendors, then the relational/
// NoSQL/file destination types) instead of falling back to alphabetical for every existing row. A
// group not listed here (a brand-new vendor, e.g. NewEHR) isn't hidden — it just sorts after every
// listed one, alphabetically among any other unlisted groups. This is display-order polish only:
// it never affects which rows appear, only what order they appear in — the one thing about "no
// Angular change needed to display a new node" this file still can't get from the catalog alone,
// since there's no ordering field in PermissionGroup/PermissionCategory (see the RBAC investigation
// this was scoped from for why one wasn't added).
const PREFERRED_NODE_ORDER = [
  'Epic', 'Athenahealth', 'Cerner', 'Allscripts', 'Healow', 'MeditechGreenfield', 'GenericFhir',
  'Hl7v2', 'Sample', 'SqlServer', 'AzureSql', 'MySql', 'PostgreSql', 'Mongo', 'Csv', 'Sftp', 'BlobStorage',
  'FhirRepository', 'Medplum',
];

function nodeRowOrderKey(groupName: string): [number, string] {
  const index = PREFERRED_NODE_ORDER.indexOf(groupName);
  return [index === -1 ? PREFERRED_NODE_ORDER.length : index, groupName];
}

/** Builds the Workflow Nodes section from the canonical Node Catalog — only entries with a
 *  dedicated permission group are rows (an ungated type has nothing to grant/toggle); rollout
 *  status is intentionally NOT a filter here — a role can be granted a not-yet-rolled-out node's
 *  permissions ahead of launch, same as before this consolidation. See this file's module doc
 *  comment for the full rationale. Returns a section with zero rows (never omitted entirely) if the
 *  catalog hasn't loaded yet, so the section header/note still render consistently. */
function buildWorkflowNodesSection(nodeCatalog: readonly NodeCatalogEntry[]): MatrixSection {
  const rows: MatrixRow[] = nodeCatalog
    .filter((entry): entry is NodeCatalogEntry & { permissionGroup: string } => entry.permissionGroup !== null)
    .slice()
    .sort((a, b) => {
      const [ai, an] = nodeRowOrderKey(a.permissionGroup);
      const [bi, bn] = nodeRowOrderKey(b.permissionGroup);
      return ai !== bi ? ai - bi : an.localeCompare(bn);
    })
    .map(entry => ({
      id: entry.permissionGroup.toLowerCase(),
      label: entry.displayName,
      actions: entry.actions.map(a => ({
        label: a.action,
        code: a.code,
        tooltipOverride: a.action.toLowerCase() === 'delete' ? NODE_DELETE_TOOLTIP_OVERRIDE : undefined,
      })),
    }));

  return {
    id: 'workflow-nodes',
    label: 'Workflow Nodes',
    note: 'Delete = permission to remove this node type from a workflow. Removing an Epic node, for example, requires both Workflow → Edit and Epic → Delete. This same Epic → Delete permission is also required (alongside Source Connections → Delete) to delete the stored Epic connection itself in Settings.',
    rows,
  };
}

/** The full section list for a given moment's live Node Catalog — every hand-authored section from
 *  STATIC_SECTIONS, unchanged, plus the dynamically-built Workflow Nodes section inserted at the
 *  same position it's always occupied (right after the Workflow module section), so the screen's
 *  overall layout is pixel-identical to before for anyone not looking at the node rows themselves. */
export function buildMatrixSections(nodeCatalog: readonly NodeCatalogEntry[]): MatrixSection[] {
  const sections = [...STATIC_SECTIONS];
  const insertAt = sections.findIndex(s => s.id === 'workflow-module') + 1;
  sections.splice(insertAt, 0, buildWorkflowNodesSection(nodeCatalog));
  return sections;
}

// ─── Pure helpers over a built section list — no catalog/selection state involved ────────────────

/** Every distinct permission code a given section list can toggle — scopes "select all"/the
 *  toolbar's "X of Y selected" count to exactly what this screen manages. A handful of existing
 *  permissions with no menu home (Pipeline.Execute, Report.View, Payload.View, the unused
 *  per-vendor Read/Assign/Execute actions on Epic/Athenahealth/Cerner) are intentionally excluded —
 *  this screen never adds or removes them, so a role's existing grant of any of those passes
 *  through save untouched. */
export function allMatrixCodes(sections: MatrixSection[]): string[] {
  return [...new Set(
    sections.flatMap(section => section.rows.flatMap(row => row.actions?.map(a => a.code) ?? []))
  )];
}
