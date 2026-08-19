export interface Source {
  id: string;
  abbr: string;
  color: string;
  context: string;
  name: string;
  sub: string;
  /** This vendor's PermissionGroupCode name, lowercased (e.g. "epic") — the node has its own full
   *  View/Create/Edit/Delete/Execute permission set (`{prefix}.view`, `{prefix}.create`, etc.), all
   *  independent of one another. See node-library-dialog.component.ts's Rank 0 mapping (tile
   *  visibility = `{prefix}.view`) and workflow-builder.component.ts's onSourceSelected (adding a
   *  new node = `{prefix}.create`; editing an existing one = `{prefix}.edit`). Omitted for a source
   *  with no dedicated permission group of its own (falls back to the generic sourceconnections.*
   *  permissions backend-side, so no per-vendor RBAC gating applies to it here). */
  permissionPrefix?: string;
}
