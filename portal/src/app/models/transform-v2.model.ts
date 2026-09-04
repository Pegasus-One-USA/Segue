import { DestinationTypeV2 } from './destination-configuration-v2.model';
export { DestinationTypeV2, SQL_FAMILY_DESTINATION_TYPES } from './destination-configuration-v2.model';

export interface Transform {
  id: string;
  rank: number;
  group?: string;
  category?: string;
  name: string;
  sub: string;
  /** For Rank-7 destination nodes: the backend DestinationType this catalog entry maps to. This makes
   *  transforms.data.ts the single source of truth for "which destinations exist" — the Destination
   *  Connections list's type filter derives its options (and category grouping) from these entries
   *  instead of keeping its own hand-maintained list, so a new destination added here shows up in both
   *  the workflow builder and the filter automatically. Unset for non-destination steps. */
  destinationType?: DestinationTypeV2;
  /** This destination type's PermissionGroupCode name, lowercased (e.g. "sqlserver") — the node has
   *  its own full View/Create/Edit/Delete/Execute permission set, all independent of one another. See
   *  node-library-dialog.component.ts's Rank 7 mapping (tile visibility = `{prefix}.view`) and
   *  workflow-builder.component.ts's onTransformSelected (adding a new node = `{prefix}.create`;
   *  editing an existing one = `{prefix}.edit`). Only set for destination types with their own
   *  dedicated permission group; every other Transform (non-destination steps, and destination types
   *  with no dedicated group) is ungated here. */
  permissionPrefix?: string;
}

/** V2's simplified straight-chain sequence: Source → Destination → [Mapping →] Transformation →
 *  De-identification. Mapping (rank 2) only applies when the destination is SQL-family (see
 *  SQL_FAMILY_DESTINATION_TYPES); Transformation and De-identification apply regardless of destination
 *  type. No branching/merge in this chain. */
export const RANK_LABEL: Record<number, string> = {
  0: 'Source',
  1: 'Destination',
  2: 'Mapping',
  3: 'Transformation',
  4: 'De-identification',
};
