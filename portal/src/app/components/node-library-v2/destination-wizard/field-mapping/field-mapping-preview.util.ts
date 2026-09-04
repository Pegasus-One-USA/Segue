import { MappingRow } from './field-mapping-model';

/**
 * FhirElement carries no example-value field, so sample values are synthesized purely from
 * valueType — good enough for a design-time "what would this look like" preview, not real data.
 */
export function sampleValueFor(valueType: string | undefined): string | number | boolean {
  switch (valueType) {
    case 'Integer': return 1;
    case 'Decimal': return 1.0;
    case 'Boolean': return true;
    case 'Date': return new Date().toISOString().slice(0, 10);
    case 'DateTime': return new Date().toISOString();
    case 'Json': return '{"...":"..."}';
    default: return 'Sample string';
  }
}

function valueForRow(row: MappingRow): string | number | boolean {
  if (row.mode === 'childJson') return '{"...":"..."}';
  if (!row.sources.length) return '';
  if (row.sources.length === 1) return sampleValueFor(row.sources[0].valueType);
  return row.sources.map(s => String(sampleValueFor(s.valueType))).join(row.delimiter ?? ', ');
}

function sqlLiteral(value: string | number | boolean): string {
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return `'${String(value).replace(/'/g, "''")}'`;
}

function csvField(value: string | number | boolean, delimiter: string): string {
  const s = String(value);
  return new RegExp(`["${escapeForRegex(delimiter)}\\n]`).test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

function escapeForRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** One INSERT INTO statement using every mapped column for this resource, with synthesized sample values. */
export function buildSqlInsert(tableName: string, rows: MappingRow[]): string {
  if (!rows.length) return '-- no columns mapped yet';
  const columns = rows.map(r => r.targetName);
  const values = rows.map(r => sqlLiteral(valueForRow(r)));
  return `INSERT INTO ${tableName || '(unnamed table)'} (${columns.join(', ')})\nVALUES (${values.join(', ')});`;
}

/** A header line + one sample data row, honoring the wizard's chosen CSV delimiter. */
export function buildCsvPreview(rows: MappingRow[], delimiter: string): string {
  if (!rows.length) return '-- no columns mapped yet';
  const header = rows.map(r => r.targetName).join(delimiter);
  const values = rows.map(r => csvField(valueForRow(r), delimiter)).join(delimiter);
  return `${header}\n${values}`;
}

export const CSV_DELIMITERS: Record<string, string> = { comma: ',', pipe: '|', tab: '\t' };
