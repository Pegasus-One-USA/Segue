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
  /** Groups this entry under another catalog entry in the Node Library picker, which renders it as an
   *  expandable parent with its surfaces nested beneath (e.g. Microsoft Fabric → OneLake Files / Warehouse).
   *
   *  Set on the CHILD, naming the parent's id. A parent is any entry that at least one other entry points at;
   *  it is a heading, never itself selectable, so it needs no flag of its own and no wizard route. Vendors
   *  whose surfaces differ enough to be separate destination types — different protocol, different
   *  capabilities — use this so the picker shows one vendor row instead of several sibling rows that read as
   *  unrelated products. A vendor with a single surface sets nothing and stays a plain flat row. */
  parentId?: string;
  /** For a Fabric entry whose surface is a LANDING MODE rather than its own destination type: the
   *  `dest_fabricMode` this row selects, pinned into the Fabric form so the picker row and the form agree.
   *
   *  Two of Fabric's surfaces earned their own DestinationType because they answer differently to what the
   *  system asks a destination — a Warehouse speaks TDS and has a live queryable schema, so field mapping
   *  applies to it (see DestinationType.DataFabricWarehouse). Lakehouse Delta does not: it is OneLake over the
   *  blob endpoint with no live schema, exactly like OneLake Files, and differs only in writing a _delta_log
   *  beside the Parquet. Promoting it to its own type would add an enum value that every call site must learn
   *  while answering every one of those questions identically to DataFabricAzure.
   *
   *  So it stays a mode and this field carries the distinction the picker needs. Unset for every other entry,
   *  including OneLake Files, which is the Fabric form's own default. */
  fabricMode?: string;
}

/** V2's simplified straight-chain sequence: Source → Destination → [Mapping →] Transformation →
 *  De-identification. Mapping (rank 2) only applies when the destination is SQL-family (see
 *  SQL_FAMILY_DESTINATION_TYPES); Transformation and De-identification apply regardless of destination
 *  type. No branching/merge in this chain. */
/** The MPI step's rank: after the source it follows (0) and before any destination (1), which is where it sits on
 *  the canvas (Source → MPI → …) and so where it sorts in the node library and in port-connect rank checks. */
export const MPI_RANK = 0.5;

export const RANK_LABEL: Record<number, string> = {
  0: 'Source',
  [MPI_RANK]: 'Master Patient Index',
  1: 'Destination',
  2: 'Mapping',
  3: 'Transformation',
  4: 'De-identification',
};
