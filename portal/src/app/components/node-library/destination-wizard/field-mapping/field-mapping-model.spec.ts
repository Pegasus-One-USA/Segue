import {
  migrateLegacyRow, serializeRowsFlat, resolveArrayPolicy, isApproximated, isSqlFamilyDestType,
  MappingRow, LegacyMappingRow, MappingDestType,
} from './field-mapping-model';
import { DestinationTable } from '../../../../services/destination-schema.service';

describe('isSqlFamilyDestType', () => {
  it('is true for SQL Server, MySQL, and PostgreSQL — the relational, table/column destinations', () => {
    expect(isSqlFamilyDestType('sql')).toBeTrue();
    expect(isSqlFamilyDestType('mysql')).toBeTrue();
    expect(isSqlFamilyDestType('postgres')).toBeTrue();
  });

  it('is false for CSV, Mongo, Blob, Medplum, and FHIR — the non-relational destinations', () => {
    const nonSqlTypes: MappingDestType[] = ['csv', 'mongo', 'blob', 'medplum', 'fhir'];
    for (const t of nonSqlTypes) {
      expect(isSqlFamilyDestType(t)).withContext(t).toBeFalse();
    }
  });
});

describe('migrateLegacyRow', () => {
  it('defaults instance to {type:"first"} — not "all" — to preserve current buildMapping() behavior', () => {
    const legacy: LegacyMappingRow = {
      resource: 'Patient', field: 'First Name', path: 'Patient.name.given',
      target: 'dbo.Patient', column: 'FirstName', jsonPath: '$.name[*].given[*]',
      valueType: 'String', arrays: ['name'],
    };
    const migrated = migrateLegacyRow(legacy);
    expect(migrated.instance).toEqual({ type: 'first' });
    expect(migrated.mode).toBe('value');
    expect(migrated.sources).toEqual([{
      fhirPath: 'Patient.name.given', label: 'First Name',
      jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'],
    }]);
    expect(migrated.targetName).toBe('FirstName');
    expect(migrated.tableName).toBe('dbo.Patient');
  });
});

describe('serializeRowsFlat', () => {
  it('emits one legacy-shaped entry per row using the primary (first) source', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient',
      sources: [
        { fhirPath: 'Patient.name.given', label: 'First Name', jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'] },
        { fhirPath: 'Patient.name.family', label: 'Last Name', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
      ],
      mode: 'value',
      delimiter: ' ',
      instance: { type: 'first' },
      targetName: 'FullName',
      tableName: 'dbo.Patient',
    }];
    const flat = serializeRowsFlat(rows, { Patient: 'dbo.Patient' });
    expect(flat.length).toBe(1);
    expect(flat[0].field).toBe('First Name');
    expect(flat[0].path).toBe('Patient.name.given');
    expect(flat[0].column).toBe('FullName');
    expect(flat[0].target).toBe('dbo.Patient');
    expect(flat[0].approximated).toBeTrue(); // join has no backend representation
  });

  it('sets valueType to Json and arrayPolicy to StoreJson for childJson rows', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'name',
      targetName: 'NameJson', tableName: 'dbo.Patient',
    }];
    const flat = serializeRowsFlat(rows, {});
    expect(flat[0].arrayPolicy).toBe('StoreJson');
    expect(flat[0].valueType).toBe('Json');
    expect(flat[0].approximated).toBeFalse();
  });

  describe('isUpsertKey', () => {
    function idRow(overrides: Partial<MappingRow> = {}): MappingRow {
      return {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.id', label: 'Id', jsonPath: '$.id' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'PatientId', tableName: 'dbo.Patient',
        ...overrides,
      };
    }
    function mrnRow(overrides: Partial<MappingRow> = {}): MappingRow {
      return {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.identifier.value', label: 'MRN' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'Mrn', tableName: 'dbo.Patient',
        ...overrides,
      };
    }
    const sqlTables: DestinationTable[] = [{
      schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient',
      columns: [
        { name: 'PatientId', dataType: 'bigint', mappingValueType: 'Number', isNullable: false, maxLength: null, isPrimaryKey: true },
        { name: 'Mrn', dataType: 'nvarchar', mappingValueType: 'String', isNullable: true, maxLength: 50 },
      ],
    }];

    it('auto-detects the real PK column as the key when nothing is explicitly designated', () => {
      const flat = serializeRowsFlat([idRow(), mrnRow()], { Patient: 'dbo.Patient' }, sqlTables);
      expect(flat.find(f => f.column === 'PatientId')?.isUpsertKey).toBeTrue();
      expect(flat.find(f => f.column === 'Mrn')?.isUpsertKey).toBeFalse();
    });

    it('an explicit isUpsertKey on a non-PK column overrides PK auto-detection entirely', () => {
      const flat = serializeRowsFlat(
        [idRow(), mrnRow({ isUpsertKey: true })],
        { Patient: 'dbo.Patient' },
        sqlTables,
      );
      expect(flat.find(f => f.column === 'Mrn')?.isUpsertKey).toBeTrue();
      // The real PK column no longer counts once another row has been explicitly designated.
      expect(flat.find(f => f.column === 'PatientId')?.isUpsertKey).toBeFalse();
    });
  });

  describe('child tables', () => {
    const targetByResource = { Patient: 'dbo.Patient' };
    const childRelations = {
      'dbo.PatientName': { parentTable: 'dbo.Patient', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' },
    };

    function childRow(overrides: Partial<MappingRow> = {}): MappingRow {
      return {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.name.use', label: 'Use' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'Use', tableName: 'dbo.PatientName',
        ...overrides,
      };
    }

    it('keeps a row targeted at a genuine child table on that table, not the resource\'s primary table', () => {
      const flat = serializeRowsFlat([childRow()], targetByResource, [], childRelations);
      expect(flat[0].target).toBe('dbo.PatientName');
      expect(flat[0].column).toBe('Use');
    });

    it('stamps parentTable/parentKeyColumn/foreignKeyColumn from the declared relation', () => {
      const flat = serializeRowsFlat([childRow()], targetByResource, [], childRelations);
      expect(flat[0].parentTable).toBe('dbo.Patient');
      expect(flat[0].parentKeyColumn).toBe('Id');
      expect(flat[0].foreignKeyColumn).toBe('PatientId');
    });

    it('omits relation fields for an ordinary same-table row', () => {
      const flat = serializeRowsFlat(
        [{ resource: 'Patient', sources: [{ fhirPath: 'Patient.active', label: 'Active' }], mode: 'value', instance: { type: 'first' }, targetName: 'Active', tableName: 'dbo.Patient' }],
        targetByResource, [], childRelations,
      );
      expect(flat[0].parentTable).toBeUndefined();
    });

    it('ignores a declared relation whose parent isn\'t this resource\'s own primary table (cross-resource FK)', () => {
      const unrelated = { 'dbo.PatientName': { parentTable: 'dbo.SomeOtherResource', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' } };
      const flat = serializeRowsFlat([childRow()], targetByResource, [], unrelated);
      expect(flat[0].parentTable).toBeUndefined();
    });

    it('translates a fanned-out array on a child table from RepeatParent to SeparateDestination', () => {
      const flat = serializeRowsFlat(
        [childRow({ sources: [{ fhirPath: 'Patient.name.given', label: 'Given', arrays: ['name'] }], instance: { type: 'all', aggregate: 'rows' } })],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('SeparateDestination');
    });

    it('leaves a root-table array field as RepeatParent', () => {
      const flat = serializeRowsFlat(
        [{ resource: 'Patient', sources: [{ fhirPath: 'Patient.name.given', label: 'Given', arrays: ['name'] }], mode: 'value', instance: { type: 'all', aggregate: 'rows' }, targetName: 'Given', tableName: 'dbo.Patient' }],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('RepeatParent');
    });
  });
});

describe('resolveArrayPolicy', () => {
  const baseSource = { fhirPath: 'Patient.name.given', label: 'First Name' };
  const arraySource = { ...baseSource, arrays: ['name'] };
  const scalarSource = { ...baseSource, arrays: [] };

  function row(overrides: Partial<MappingRow>): MappingRow {
    return {
      resource: 'Patient', sources: [arraySource], mode: 'value',
      targetName: 'FirstName', tableName: 'dbo.Patient', ...overrides,
    };
  }

  it('childJson mode -> StoreJson, not approximated', () => {
    expect(resolveArrayPolicy(row({ mode: 'childJson', sources: [], childNodeId: 'name' })))
      .toEqual({ arrayPolicy: 'StoreJson', approximated: false });
  });

  it('single source, instance "first" -> FirstItem, not approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'first' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false });
  });

  it('single source, instance "all"/aggregate rows, has array ancestors -> RepeatParent', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'all', aggregate: 'rows' } })))
      .toEqual({ arrayPolicy: 'RepeatParent', approximated: false });
  });

  it('single source, instance "all", no array ancestors -> Scalar', () => {
    expect(resolveArrayPolicy(row({ sources: [scalarSource], instance: { type: 'all', aggregate: 'rows' } })))
      .toEqual({ arrayPolicy: 'Scalar', approximated: false });
  });

  it('single source, instance "all"/aggregate csv -> FirstItem, approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'all', aggregate: 'csv' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('single source, instance "nth" -> FirstItem, approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'nth', n: 2 } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('single source, instance "criteria" -> FirstItem, approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', op: '=', value: 'official' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('joined sources (>1) -> rules applied to sources[0], always approximated', () => {
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'first' },
    });
    expect(resolveArrayPolicy(joined)).toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('absent instance defaults to "first" semantics', () => {
    expect(resolveArrayPolicy(row({ instance: undefined })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false });
  });
});

describe('isApproximated', () => {
  it('mirrors resolveArrayPolicy().approximated', () => {
    const exact: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Patient ID' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'SourcePatientId', tableName: 'dbo.Patient',
    };
    const approx: MappingRow = { ...exact, instance: { type: 'nth', n: 1 } };
    expect(isApproximated(exact)).toBeFalse();
    expect(isApproximated(approx)).toBeTrue();
  });
});
