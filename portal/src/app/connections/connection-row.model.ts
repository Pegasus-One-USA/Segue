import { InjectionToken } from '@angular/core';
import { Observable } from 'rxjs';

/** Every kind of connection the Source Connections and Destination Connections pages list. A page shows one EHR kind
 *  only (ehr-read on Sources, ehr-write on Destinations), so a Read & Write connection is one row on each page. */
export type ConnectionKind = 'ehr-read' | 'database' | 'destination' | 'ehr-write';
export type ConnectionSortKey = 'name' | 'type' | 'audience' | 'status' | 'actionOn';
export type ConnectionActionId = 'view' | 'edit' | 'test' | 'delete';
export type ConnectionSortDirection = 'asc' | 'desc';

export interface ConnectionSort {
  key: ConnectionSortKey;
  dir: ConnectionSortDirection;
}

/** One row of a merged connection list. Each kind turns its own DTO into this; the page searches, filters, sorts and
 *  pages these without knowing which kind a row came from. */
export interface ConnectionRow {
  kind: ConnectionKind;
  /** `${kind}:${id}` — the table's track key. */
  key: string;
  id: string;
  name: string;
  typeLabel: string;
  /** What the filters match on — see connection-list-query.ts. */
  filterKeys: string[];
  /** EHR read connections only. */
  audience: string | null;
  /** Base URL or destination target. */
  address: string | null;
  /** EHR read connections only. */
  clientId: string | null;
  /** EHR write connections only: "Activated" / "Not activated", or null where the vendor has no gated APIs. */
  writeApis: string | null;
  /** null = this kind has no status (databases). */
  isEnabled: boolean | null;
  actionBy: string | null;
  /** ISO date. */
  actionOn: string | null;
  /** The original DTO, read back only by the kind that made the row. */
  raw: unknown;
}

export interface ConnectionRowAction {
  id: ConnectionActionId;
  label: string;
  icon: string;
  disabled: boolean;
  danger?: boolean;
}

/** One card in the "New" picker. `value` is the kind's own choice (an EHR vendor, a destination type, 'Database'). */
export interface ConnectionKindCard {
  kind: ConnectionKind;
  value: string;
  label: string;
  sub: string;
  abbr: string;
  color: string;
}

export interface ConnectionListColumn {
  id: 'name' | 'type' | 'audience' | 'address' | 'clientId' | 'writeApis' | 'status' | 'actionBy' | 'actionOn';
  header: string;
  sortKey?: ConnectionSortKey;
}

/**
 * What a page needs from each kind of connection it lists. Each kind is its own component (it owns its forms and
 * dialogs) and provides itself under CONNECTION_KIND_HOST; the page finds them with viewChildren and dispatches by
 * `kind`, never with a switch.
 */
export interface ConnectionKindHost {
  readonly kind: ConnectionKind;
  /** Toasted when list() fails; the other kinds still show. */
  readonly loadErrorMessage: string;
  canList(): boolean;
  /** Never called when canList() is false. */
  list(): Observable<ConnectionRow[]>;
  /** [] when the role cannot create this kind. */
  createCards(): ConnectionKindCard[];
  /** true = a form opened, so the picker closes. */
  create(card: ConnectionKindCard): boolean;
  /** Already permission-filtered; [] hides the row menu. */
  rowActions(row: ConnectionRow): ConnectionRowAction[];
  runAction(id: ConnectionActionId, row: ConnectionRow): void;
  /** Rows of this kind now on screen (destinations load their execution-history flags for these). */
  rowsShown?(rows: ConnectionRow[]): void;
}

export const CONNECTION_KIND_HOST = new InjectionToken<ConnectionKindHost>('CONNECTION_KIND_HOST');
