export interface Transform {
  id: string;
  rank: number;
  group?: string;
  category?: string;
  name: string;
  sub: string;
  /** This destination type's PermissionGroupCode name, lowercased (e.g. "sqlserver") — the node has
   *  its own full View/Create/Edit/Delete/Execute permission set, all independent of one another. See
   *  node-library-dialog.component.ts's Rank 7 mapping (tile visibility = `{prefix}.view`) and
   *  workflow-builder.component.ts's onTransformSelected (adding a new node = `{prefix}.create`;
   *  editing an existing one = `{prefix}.edit`). Only set for destination types with their own
   *  dedicated permission group; every other Transform (non-destination steps, and destination types
   *  with no dedicated group) is ungated here. */
  permissionPrefix?: string;
}

export const RANK_LABEL: Record<number, string> = {
  0: 'Source',
  1: 'Consent',
  2: 'Validation',
  3: 'Normalize',
  4: 'Terminology',
  5: 'De-identify',
  6: 'Map / reshape',
  7: 'Destination',
  8: 'Audit & lineage',
  9: 'Analytics',
};
