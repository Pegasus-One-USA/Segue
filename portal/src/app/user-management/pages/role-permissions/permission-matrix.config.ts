// user-management/pages/role-permissions/permission-matrix.config.ts
//
// Hand-authored presentation layout for the Role Permissions screen — a flat row/column matrix,
// grouped into the same sections as the app's real menus (Access Control, Workflows, Settings,
// Logs & Compliance) but with NO tree nesting and NO cross-row dependencies: every checkbox here
// controls exactly one existing permission code, independently. Introduces no new permission,
// group, or backend concept — every `code` below is a wire code (`group.action`) that already
// exists in the catalog returned by GET /api/v1/roles/permission-catalog.
//
// Deliberately has no "requires" concept at all (unlike the hierarchical tree UI this replaces):
// inspection of the backend (WorkflowEndpoints.cs's per-vendor checks, the terminology
// controllers, etc.) confirms none of these permissions actually depend on one another server-side
// — epic.edit and workflow.view are checked as two unrelated permissions, not a dependent pair.
// The old auto-check/cascade behavior was a frontend-only convenience, not a security rule, so
// removing it doesn't loosen anything the backend enforces.

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

// Every source vendor / destination type node has its own full View/Create/Edit/Delete/Execute
// set — five independent permissions, no dependency between them (checking View does not imply
// Create, etc.), matching the same five actions the Workflow row itself exposes above. `prefix`
// is the node's own PermissionGroupCode name lowercased (e.g. 'epic', 'sqlserver').
const vendor = (id: string, label: string, prefix: string): MatrixRow => ({
  id, label,
  actions: [
    { label: 'View', code: `${prefix}.view` },
    { label: 'Create', code: `${prefix}.create` },
    { label: 'Edit', code: `${prefix}.edit` },
    // {prefix}.delete does double duty by design, not by accident — it gates BOTH removing this
    // vendor's node from a workflow canvas (WorkflowEndpoints.cs's /workflows/build node-removal
    // check, added alongside workflow.edit — see canvas.component.ts's canDeleteNode()) AND deleting
    // the vendor's stored Source/Destination Connection in Settings (SourceConnectionsController.cs/
    // ConfigurationsController.cs, alongside sourceconnections.delete/destinationconnections.delete).
    // Both are "can this role delete Epic-related things," just at two different scopes, and this is
    // the only place in this whole screen either half is independently assignable.
    { label: 'Delete', code: `${prefix}.delete`, tooltipOverride: 'Allows removing this node type from a workflow. Also required (alongside Source/Destination Connections → Delete) to delete this vendor’s stored connection in Settings.' },
    { label: 'Execute', code: `${prefix}.execute` },
  ],
});

const terminologySystem = (id: string, label: string, codePrefix: string): MatrixRow => ({
  id, label,
  actions: [
    { label: 'View', code: `${codePrefix}.view` },
    { label: 'Write', code: `${codePrefix}.write` },
  ],
});

export const MATRIX_SECTIONS: MatrixSection[] = [
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
  // Workflow nodes — the sources and destinations usable INSIDE a workflow. Holding ANY permission
  // on ANY row here is, by itself, sufficient for Workflow module access (see the section above) —
  // that's an effective-access rule evaluated at request/render time, not a dependency stored here
  // or cascaded when a checkbox changes. Every row's five actions stay fully independent of every
  // other row and of the Workflow module row above.
  {
    id: 'workflow-nodes',
    label: 'Workflow Nodes',
    note: 'Delete = permission to remove this node type from a workflow. Removing an Epic node, for example, requires both Workflow → Edit and Epic → Delete. This same Epic → Delete permission is also required (alongside Source Connections → Delete) to delete the stored Epic connection itself in Settings.',
    rows: [
      vendor('epic', 'Epic', 'epic'),
      vendor('athenahealth', 'Athenahealth', 'athenahealth'),
      vendor('cerner', 'Cerner', 'cerner'),
      vendor('allscripts', 'Allscripts', 'allscripts'),
      // Label leads with "eClinicalWorks" — the Node Library tile for this same connector
      // (sources-v2.data.ts's 'healow' entry) shows that as its product name; see the matching comment
      // on PermissionGroupCode.Healow. Row id/prefix stay 'healow' — only the display label changes.
      vendor('healow', 'eClinicalWorks (Healow)', 'healow'),
      vendor('meditech', 'Meditech', 'meditechgreenfield'),
      vendor('genericfhir', 'Generic FHIR', 'genericfhir'),
      vendor('hl7v2', 'HL7 v2', 'hl7v2'),
      vendor('sample', 'Sample', 'sample'),
      vendor('sqlserver', 'SQL Server', 'sqlserver'),
      vendor('azuresql', 'Azure SQL', 'azuresql'),
      vendor('mysql', 'MySQL', 'mysql'),
      vendor('postgresql', 'PostgreSQL', 'postgresql'),
      vendor('mongo', 'MongoDB', 'mongo'),
      vendor('csv', 'CSV', 'csv'),
      vendor('sftp', 'SFTP', 'sftp'),
      vendor('blobstorage', 'Azure Blob Storage', 'blobstorage'),
      // Cloud/FHIR destinations (transforms-v2.data.ts, rank 1) — newly given their own dedicated
      // PermissionGroupCode members instead of falling back to the generic Source Connections
      // permission. Labels match the product each one actually writes to (Aidbox/Medplum/Azure Health
      // Data Services), not the raw DestinationType enum name.
      vendor('fhirrepository', 'Aidbox', 'fhirrepository'),
      vendor('medplum', 'Medplum (FHIR)', 'medplum'),
      vendor('azurefhirservice', 'Azure FHIR Service', 'azurefhirservice'),
    ],
  },
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
          { label: 'Create', code: 'destinationconnections.create' },
          { label: 'Edit', code: 'destinationconnections.edit' },
          { label: 'Deactivate', code: 'destinationconnections.deactivate' },
          { label: 'Delete', code: 'destinationconnections.delete' },
          { label: 'Test', code: 'destinationconnections.test' },
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

// ─── Pure helpers over the static config — no catalog/selection state involved ────────────────

/** Every distinct permission code the matrix can toggle — scopes "select all"/the toolbar's
 *  "X of Y selected" count to exactly what this screen manages. A handful of existing permissions
 *  with no menu home (Pipeline.Execute, Report.View, Payload.View, the unused per-vendor Read/
 *  Assign/Execute actions on Epic/Athenahealth/Cerner) are intentionally excluded — this screen
 *  never adds or removes them, so a role's existing grant of any of those passes through save
 *  untouched. */
export const ALL_MATRIX_CODES: string[] = [...new Set(
  MATRIX_SECTIONS.flatMap(section => section.rows.flatMap(row => row.actions?.map(a => a.code) ?? []))
)];
