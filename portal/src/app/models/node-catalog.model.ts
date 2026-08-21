// models/node-catalog.model.ts
//
// The single canonical Node Catalog — shared source of truth for the Workflow Builder Node Library
// AND Role Permissions' "Workflow Nodes" section. Replaces the former hand-maintained SOURCES/
// TRANSFORMS destination-type rows, PhaseConfigService's enabledSourceIds/enabledTransformIds, and
// the free-typed `permissionPrefix` field — every node's identity, permission codes, and implemented
// status now come from one backend response (GET /api/v1/permissions/node-catalog) instead of being
// kept in sync by hand across multiple files.

export interface NodeCatalogActionDto {
  action: string;
  code: string;
}

export interface NodeCatalogEntryDto {
  kind: string;
  type: string;
  displayName: string;
  subtitle: string | null;
  category: string | null;
  icon: string | null;
  color: string | null;
  implemented: boolean;
  permissionGroup: string | null;
  actions: NodeCatalogActionDto[];
}

export interface NodeCatalogAction {
  /** "View" | "Create" | "Edit" | "Delete" | "Execute" */
  action: string;
  /** The exact wire permission code, e.g. "epic.view" — the same code `/permissions/catalog` uses,
   *  so a `hasPermission(code)` check or a Role Permissions save-payload id lookup needs no
   *  separate resolution path. */
  code: string;
}

export interface NodeCatalogEntry {
  kind: 'Source' | 'Destination';
  /**
   * The canonical identifier — the exact backend `SourceSystemType`/`DestinationType` enum member
   * name (e.g. 'Epic', 'FhirRepository', 'Medplum'). Never a hand-typed id, never a separate
   * permission-prefix string to keep in sync.
   */
  type: string;
  displayName: string;
  subtitle: string | null;
  category: string | null;
  icon: string | null;
  color: string | null;
  /** Real, working end-to-end implementation status — a permanent technical fact, not a
   *  phase/rollout decision. Replaces PhaseConfigService's hardcoded id arrays. Node Library
   *  visibility is exactly `implemented && user has this node's own .view permission` — never
   *  gated by any separate rollout/allowlist concept. */
  implemented: boolean;
  /** The PermissionGroupCode name governing this node, or null if it has no dedicated group (and is
   *  therefore ungated — never substitute an unrelated permission like `sourceconnections.*` here). */
  permissionGroup: string | null;
  /** Empty when `permissionGroup` is null. Already excludes legacy `.read`/`.assign` actions. */
  actions: NodeCatalogAction[];
}

export function mapNodeCatalogEntryDto(dto: NodeCatalogEntryDto): NodeCatalogEntry {
  return {
    kind: dto.kind === 'Source' ? 'Source' : 'Destination',
    type: dto.type,
    displayName: dto.displayName,
    subtitle: dto.subtitle,
    category: dto.category,
    icon: dto.icon,
    color: dto.color,
    implemented: dto.implemented,
    permissionGroup: dto.permissionGroup,
    actions: (dto.actions ?? []).map(a => ({ action: a.action, code: a.code })),
  };
}

/** Looks up a node's own permission code for a given action (case-insensitive on `action`), e.g.
 *  `actionCode(entry, 'View')` → 'epic.view'. Returns null for an ungated node (no permission group)
 *  or an action that entry doesn't expose. */
export function actionCode(entry: NodeCatalogEntry, action: 'View' | 'Create' | 'Edit' | 'Delete' | 'Execute'): string | null {
  return entry.actions.find(a => a.action.toLowerCase() === action.toLowerCase())?.code ?? null;
}
