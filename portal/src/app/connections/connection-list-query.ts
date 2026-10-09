import { ConnectionRow, ConnectionSort, ConnectionSortKey } from './connection-row.model';

export interface ConnectionListQuery {
  search: string;
  /** Every non-empty key must be on the row. */
  filterKeys: string[];
  /** Only rows that have a status are filtered by it: a kind with none (databases) always passes. */
  status: '' | 'true' | 'false';
  sort: ConnectionSort;
  pageIndex: number;
  pageSize: number;
}

export interface ConnectionListPage {
  items: ConnectionRow[];
  total: number;
  /** The page actually shown: clamped when the requested one is past the end (e.g. after the last row of the last
   *  page was deleted). The page binds the paginator to this. */
  pageIndex: number;
}

type SortValue = string | number | null;

const SORT_VALUES: Record<ConnectionSortKey, (row: ConnectionRow) => SortValue> = {
  name: row => row.name,
  type: row => row.typeLabel,
  audience: row => row.audience,
  // Disabled before enabled; no status sorts last.
  status: row => (row.isEnabled === null ? null : Number(row.isEnabled)),
  actionOn: row => {
    const time = row.actionOn ? Date.parse(row.actionOn) : NaN;
    return Number.isNaN(time) ? null : time;
  },
};

function compareValues(a: SortValue, b: SortValue): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, { sensitivity: 'base' });
}

function byName(a: ConnectionRow, b: ConnectionRow): number {
  return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
}

/** Search, filter, sort and page a merged connection list. Pure: the page runs it inside a computed. */
export function queryConnectionRows(rows: readonly ConnectionRow[], query: ConnectionListQuery): ConnectionListPage {
  const term = query.search.trim().toLowerCase();
  const keys = query.filterKeys.filter(k => !!k);
  const value = SORT_VALUES[query.sort.key];
  const direction = query.sort.dir === 'desc' ? -1 : 1;

  const matched = rows
    .filter(row => !term
      || row.name.toLowerCase().includes(term)
      || row.typeLabel.toLowerCase().includes(term)
      || (row.address ?? '').toLowerCase().includes(term))
    .filter(row => keys.every(k => row.filterKeys.includes(k)))
    .filter(row => query.status === '' || row.isEnabled === null || row.isEnabled === (query.status === 'true'))
    .sort((a, b) => {
      const va = value(a);
      const vb = value(b);
      // Nulls last in both directions.
      if (va === null && vb !== null) return 1;
      if (vb === null && va !== null) return -1;
      const order = va === null ? 0 : compareValues(va, vb) * direction;
      return order !== 0 ? order : byName(a, b);
    });

  const pageSize = Math.max(1, query.pageSize);
  const lastPage = Math.max(0, Math.ceil(matched.length / pageSize) - 1);
  const pageIndex = Math.min(Math.max(0, query.pageIndex), lastPage);
  return {
    items: matched.slice(pageIndex * pageSize, (pageIndex + 1) * pageSize),
    total: matched.length,
    pageIndex,
  };
}
