import { sampleValueFor, buildSqlInsert, buildCsvPreview, CSV_DELIMITERS } from './field-mapping-preview.util';
import { MappingRow } from './field-mapping-model';

describe('sampleValueFor', () => {
  it('returns a representative value per valueType', () => {
    expect(sampleValueFor('Integer')).toBe(1);
    expect(sampleValueFor('Decimal')).toBe(1.0);
    expect(sampleValueFor('Boolean')).toBe(true);
    expect(sampleValueFor('Json')).toBe('{"...":"..."}');
    expect(sampleValueFor('String')).toBe('Sample string');
    expect(sampleValueFor(undefined)).toBe('Sample string');
    expect(typeof sampleValueFor('Date')).toBe('string');
    expect(typeof sampleValueFor('DateTime')).toBe('string');
  });
});

const ROWS: MappingRow[] = [
  { resource: 'Patient', sources: [{ fhirPath: 'Patient.name.given', label: 'First Name', valueType: 'String' }], mode: 'value', instance: { type: 'first' }, targetName: 'FirstName', tableName: 'dbo.Patient' },
  { resource: 'Patient', sources: [{ fhirPath: 'Patient.birthDate', label: 'DOB', valueType: 'Date' }], mode: 'value', instance: { type: 'first' }, targetName: 'DateOfBirth', tableName: 'dbo.Patient' },
];

describe('buildSqlInsert', () => {
  it('generates one INSERT with every mapped column', () => {
    const sql = buildSqlInsert('dbo.Patient', ROWS);
    expect(sql).toContain('INSERT INTO dbo.Patient (FirstName, DateOfBirth)');
    expect(sql).toContain("'Sample string'");
  });

  it('handles no rows', () => {
    expect(buildSqlInsert('dbo.Patient', [])).toBe('-- no columns mapped yet');
  });

  it('quotes join delimiters correctly', () => {
    const joined: MappingRow = {
      resource: 'Patient',
      sources: [
        { fhirPath: 'Patient.name.given', label: 'First', valueType: 'String' },
        { fhirPath: 'Patient.name.family', label: 'Last', valueType: 'String' },
      ],
      mode: 'value', delimiter: ' ', instance: { type: 'first' }, targetName: 'FullName', tableName: 'dbo.Patient',
    };
    const sql = buildSqlInsert('dbo.Patient', [joined]);
    expect(sql).toContain("'Sample string Sample string'");
  });
});

describe('buildCsvPreview', () => {
  it('generates a header line and one sample row with the comma delimiter', () => {
    const csv = buildCsvPreview(ROWS, CSV_DELIMITERS['comma']);
    const lines = csv.split('\n');
    expect(lines[0]).toBe('FirstName,DateOfBirth');
    expect(lines[1].split(',').length).toBe(2);
  });

  it('honors the pipe delimiter', () => {
    const csv = buildCsvPreview(ROWS, CSV_DELIMITERS['pipe']);
    expect(csv.split('\n')[0]).toBe('FirstName|DateOfBirth');
  });

  it('handles no rows', () => {
    expect(buildCsvPreview([], ',')).toBe('-- no columns mapped yet');
  });
});
