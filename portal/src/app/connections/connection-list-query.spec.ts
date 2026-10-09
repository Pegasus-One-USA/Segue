import { queryConnectionRows, ConnectionListQuery } from './connection-list-query';
import { ConnectionRow, ConnectionSortKey } from './connection-row.model';

function row(partial: Partial<ConnectionRow> & { id: string; name: string }): ConnectionRow {
  return {
    kind: 'ehr-read', key: `ehr-read:${partial.id}`, typeLabel: 'Epic', filterKeys: [], audience: null, address: null,
    clientId: null, writeApis: null, isEnabled: true, actionBy: null, actionOn: null, raw: null, ...partial,
  };
}

const query = (q: Partial<ConnectionListQuery> = {}): ConnectionListQuery => ({
  search: '', filterKeys: [], status: '', sort: { key: 'name', dir: 'asc' }, pageIndex: 0, pageSize: 10, ...q,
});

const names = (rows: ConnectionRow[]) => rows.map(r => r.name);

describe('queryConnectionRows', () => {
  const ROWS: ConnectionRow[] = [
    row({ id: '1', name: 'Bravo', typeLabel: 'Epic', address: 'https://epic.example', filterKeys: ['kind:ehr-read', 'vendor:Epic'], isEnabled: true, audience: 'Backend System', actionOn: '2026-01-02T00:00:00Z' }),
    row({ id: '2', name: 'alpha', kind: 'database', typeLabel: 'SQL database (PostgreSQL)', filterKeys: ['kind:database', 'engine:postgresql'], isEnabled: null, actionOn: null }),
    row({ id: '3', name: 'Charlie', typeLabel: 'eClinicalWorks', address: 'https://ECW.example', filterKeys: ['kind:ehr-read', 'vendor:Healow', 'audience:Patient'], isEnabled: false, audience: 'Patient', actionOn: '2026-03-01T00:00:00Z' }),
  ];

  it('returns every row for an empty search', () => {
    expect(queryConnectionRows(ROWS, query()).total).toBe(3);
  });

  it('searches name, type and address, ignoring case', () => {
    expect(names(queryConnectionRows(ROWS, query({ search: 'ALPHA' })).items)).toEqual(['alpha']);
    expect(names(queryConnectionRows(ROWS, query({ search: 'postgres' })).items)).toEqual(['alpha']);
    expect(names(queryConnectionRows(ROWS, query({ search: 'ecw.example' })).items)).toEqual(['Charlie']);
    expect(names(queryConnectionRows(ROWS, query({ search: '  bravo ' })).items)).toEqual(['Bravo']);
  });

  it('requires every filter key (AND)', () => {
    expect(names(queryConnectionRows(ROWS, query({ filterKeys: ['kind:ehr-read'] })).items)).toEqual(['Bravo', 'Charlie']);
    expect(names(queryConnectionRows(ROWS, query({ filterKeys: ['kind:ehr-read', 'audience:Patient'] })).items)).toEqual(['Charlie']);
    expect(queryConnectionRows(ROWS, query({ filterKeys: ['kind:database', 'vendor:Epic'] })).total).toBe(0);
    expect(queryConnectionRows(ROWS, query({ filterKeys: ['', ''] })).total).toBe(3);
  });

  it('a status filter keeps matching rows, and keeps rows that have no status (a database under "Active")', () => {
    expect(names(queryConnectionRows(ROWS, query({ status: 'true' })).items)).toEqual(['alpha', 'Bravo']);
    expect(names(queryConnectionRows(ROWS, query({ status: 'false' })).items)).toEqual(['alpha', 'Charlie']);
    expect(names(queryConnectionRows(ROWS, query({ status: 'true', filterKeys: ['kind:database'] })).items)).toEqual(['alpha']);
  });

  const expectations: Record<ConnectionSortKey, { asc: string[]; desc: string[] }> = {
    name: { asc: ['alpha', 'Bravo', 'Charlie'], desc: ['Charlie', 'Bravo', 'alpha'] },
    type: { asc: ['Charlie', 'Bravo', 'alpha'], desc: ['alpha', 'Bravo', 'Charlie'] },
    // alpha has no audience: last both ways.
    audience: { asc: ['Bravo', 'Charlie', 'alpha'], desc: ['Charlie', 'Bravo', 'alpha'] },
    // Disabled before enabled; no status last both ways.
    status: { asc: ['Charlie', 'Bravo', 'alpha'], desc: ['Bravo', 'Charlie', 'alpha'] },
    actionOn: { asc: ['Bravo', 'Charlie', 'alpha'], desc: ['Charlie', 'Bravo', 'alpha'] },
  };
  (Object.keys(expectations) as ConnectionSortKey[]).forEach(key => {
    it(`sorts by ${key} both ways, nulls last`, () => {
      expect(names(queryConnectionRows(ROWS, query({ sort: { key, dir: 'asc' } })).items)).toEqual(expectations[key].asc);
      expect(names(queryConnectionRows(ROWS, query({ sort: { key, dir: 'desc' } })).items)).toEqual(expectations[key].desc);
    });
  });

  it('breaks ties by name', () => {
    const tied = [row({ id: 'a', name: 'Zed', typeLabel: 'Epic' }), row({ id: 'b', name: 'Amy', typeLabel: 'Epic' })];
    expect(names(queryConnectionRows(tied, query({ sort: { key: 'type', dir: 'desc' } })).items)).toEqual(['Amy', 'Zed']);
  });

  it('slices a page', () => {
    const page = queryConnectionRows(ROWS, query({ pageSize: 2, pageIndex: 1 }));
    expect(names(page.items)).toEqual(['Charlie']);
    expect(page.total).toBe(3);
    expect(page.pageIndex).toBe(1);
  });

  it('clamps a page past the end, e.g. after the last row on the last page is deleted', () => {
    const afterDelete = ROWS.filter(r => r.name !== 'Charlie');
    const page = queryConnectionRows(afterDelete, query({ pageSize: 2, pageIndex: 1 }));
    expect(page.pageIndex).toBe(0);
    expect(names(page.items)).toEqual(['alpha', 'Bravo']);
  });

  it('is page 0 with no rows', () => {
    expect(queryConnectionRows([], query({ pageIndex: 4 }))).toEqual({ items: [], total: 0, pageIndex: 0 });
  });
});
