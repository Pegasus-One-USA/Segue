// ── Mapping snapshot ─────────────────────────────────────────────────────────
// A self-contained, mapping-only JSON for the Map Fields screen — deliberately
// independent of the workflow (no nodes/edges/sources/destinations/trigger/
// workflowId, no connector or authentication config, no connection strings).
// Everything here is either mapping-schema data (resource type, destination
// tables/columns, mapping rows) or the minimal UI-selection state needed to
// land the screen back where the user left it. Pure client-side data model —
// MappingSnapshotService decides how (and whether) it's actually persisted.

import { DestinationTable } from '../../../../services/destination-schema.service';
import { MappingRow, MappingDestType } from './field-mapping-model';
import { ChildTableRelation } from './field-mapping-summary.model';
import { ResourceFieldDef } from '../destination-wizard.component';

export interface MappingSnapshot {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  destType: MappingDestType;
  /** Every data group the mapping wizard has open, not just the one being edited right now. */
  selectedResources: string[];
  /** Which group's canvas was open when this was saved; null = the group-list screen. */
  activeGroup: string | null;
  /** Per-resource destination table name — mirrors the wizard's own targetByResource signal. */
  targetByResource: Record<string, string>;
  /** Per-resource list of additional table names targeted alongside the primary one. */
  extraTablesByGroup: Record<string, string[]>;
  /** The full destination table/column catalog this mapping was built against, including anything
   *  the user created via "+ Add a table" / "+ Add column" (see DestinationTable.origin) — restoring
   *  from this never needs to re-probe a live database connection. */
  destinationTables: DestinationTable[];
  /** Fields derived from a pasted JSON payload (Load JSON payload), per resource, when used instead of
   *  the backend FHIR catalog. */
  payloadFieldsByResource: Record<string, ResourceFieldDef[]>;
  mappingRows: MappingRow[];
  /** Parent/FK relationship for any table created as a child, keyed by table full name — global, not
   *  per-resource (see DestinationWizardComponent.childTableRelationsByTable). */
  childTableRelationsByTable: Record<string, ChildTableRelation>;
}

/** Lightweight listing entry for the "Load saved mapping" dev control — avoids fetching every full
 *  snapshot just to populate a picker. */
export interface MappingSnapshotSummary {
  id: string;
  name: string;
  destType: MappingDestType;
  activeGroup: string | null;
  updatedAt: string;
  mappingRowCount: number;
}
