import { newCsvDatasetKey, normalizeDatasetKey, sqlDatasetKey } from './tabular-dataset-key';

/** Same rules as the API's TabularSourceSettings.NormalizeDatasetKey. */
describe('tabular data set key', () => {
  it('normalises like the API: lower case, a-z0-9 and single hyphens, 3 to 64 characters', () => {
    expect(normalizeDatasetKey('  Clinic Allergies__v2 ')).toBe('clinic-allergies-v2');
    expect(normalizeDatasetKey('ab')).toBeNull();
    expect(normalizeDatasetKey('--')).toBeNull();
    expect(normalizeDatasetKey('a'.repeat(70))!.length).toBe(64);
  });

  it('a database key comes from its id, with a short part of its name in front', () => {
    const id = '7D1C2B3A-0000-4000-8000-00000000ABCD';
    expect(sqlDatasetKey({ id, name: 'Clinic DB' })).toBe('sql-clinic-db-7d1c2b3a-0000-4000-8000-00000000abcd');
    expect(sqlDatasetKey({ id, name: null })).toBe('sql-7d1c2b3a-0000-4000-8000-00000000abcd');
    expect(sqlDatasetKey({ id: null, name: 'Clinic DB' })).toBeNull();
    expect(sqlDatasetKey(null)).toBeNull();
  });

  it('two databases with the same name never share a key', () => {
    const a = sqlDatasetKey({ id: '11111111-0000-4000-8000-000000000001', name: 'Clinic DB' });
    const b = sqlDatasetKey({ id: '22222222-0000-4000-8000-000000000002', name: 'Clinic DB' });
    expect(a).not.toBe(b);
  });

  it('a long name is cut short so the whole id still fits in 64 characters', () => {
    const id = '7d1c2b3a-0000-4000-8000-00000000abcd';
    const key = sqlDatasetKey({ id, name: 'A very long database name '.repeat(5) })!;
    expect(key.length).toBeLessThanOrEqual(64);
    expect(key.endsWith(`-${id}`)).toBeTrue();
    expect(normalizeDatasetKey(key)).toBe(key);
  });

  it('a CSV key is already in its normal form', () => {
    const key = newCsvDatasetKey();
    expect(normalizeDatasetKey(key)).toBe(key);
    expect(newCsvDatasetKey()).not.toBe(key);
  });
});
