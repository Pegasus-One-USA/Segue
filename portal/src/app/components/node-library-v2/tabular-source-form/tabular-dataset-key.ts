/**
 * The data set key names a CSV / SQL source's rows for good, so rows already written to an EHR are recognised and
 * not written twice. Same rules as the API's TabularSourceSettings.NormalizeDatasetKey: lower-case letters and
 * digits, anything else becomes '-', repeated '-' collapse, 3 to 64 characters. null when nothing usable is left.
 */
export function normalizeDatasetKey(value: string | null | undefined): string | null {
  if (!value || !value.trim()) return null;
  let key = value.trim().toLowerCase().replace(/[^a-z0-9]/g, '-').replace(/-{2,}/g, '-').replace(/^-+|-+$/g, '');
  if (key.length > 64) key = key.slice(0, 64).replace(/-+$/g, '');
  return key.length >= 3 ? key : null;
}

/**
 * A database source's key comes from the saved database's id, which never changes and is never reused, so two
 * databases with the same name (or one deleted and made again) never share a key. A short part of the name goes in
 * front only to make the key readable: "sql-<name, cut short>-<id>". null when the database has no usable id.
 */
export function sqlDatasetKey(connection: { id: string | null; name: string | null } | null): string | null {
  const id = normalizeDatasetKey(connection?.id);
  if (!id) return null;
  const name = (normalizeDatasetKey(connection?.name) ?? '').slice(0, Math.max(0, 64 - 'sql--'.length - id.length));
  return normalizeDatasetKey(name ? `sql-${name}-${id}` : `sql-${id}`);
}

/** A CSV source's key is made once, when the node is created, and kept from then on (a corrected file keeps it). */
export function newCsvDatasetKey(): string {
  const random = typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID().replace(/-/g, '')
    : Math.random().toString(16).slice(2) + Date.now().toString(16);
  return `csv-${random.slice(0, 12)}`;
}
