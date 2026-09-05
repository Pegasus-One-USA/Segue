import {
  migrateLegacyRow, serializeRowsFlat, resolveArrayPolicy, isApproximated,
  MappingRow, LegacyMappingRow,
  qualifyTableName, defaultSchemaFor, splitTableName, reconcileTargetsForDestTypeSwitch,
  effectiveMappingValueType, checkColumnTypeCompatibility,
} from './field-mapping-model';
import { DestinationTable } from '../../../../services/destination-schema.service';

describe('defaultSchemaFor', () => {
  it('SQL Server -> "dbo"', () => {
    expect(defaultSchemaFor('sql')).toBe('dbo');
  });
  it('PostgreSQL -> "public"', () => {
    expect(defaultSchemaFor('postgres')).toBe('public');
  });
  it('MySQL -> "" (no schema layer distinct from the database)', () => {
    expect(defaultSchemaFor('mysql')).toBe('');
  });
  it('non-relational types -> "" (never actually consulted, but must not throw/guess)', () => {
    expect(defaultSchemaFor('mongo')).toBe('');
    expect(defaultSchemaFor('csv')).toBe('');
  });
});

describe('qualifyTableName', () => {
  it('SQL Server: bare name -> "dbo.<name>"', () => {
    expect(qualifyTableName('Appointment', 'sql')).toBe('dbo.Appointment');
  });
  it('PostgreSQL: bare name -> "public.<name>"', () => {
    expect(qualifyTableName('Appointment', 'postgres')).toBe('public.Appointment');
  });
  it('MySQL: bare name -> unchanged (no schema layer)', () => {
    expect(qualifyTableName('Appointment', 'mysql')).toBe('Appointment');
  });
  it('already-qualified name is never re-qualified or double-qualified, for any destType', () => {
    expect(qualifyTableName('dbo.Appointment', 'sql')).toBe('dbo.Appointment');
    expect(qualifyTableName('public.Appointment', 'postgres')).toBe('public.Appointment');
    // Even a "wrong-looking" qualifier for the current destType is left alone — a "." means the caller
    // (a real user, or an already-correctly-saved document) already made a deliberate choice here.
    expect(qualifyTableName('dbo.Appointment', 'postgres')).toBe('dbo.Appointment');
    expect(qualifyTableName('myschema.Appointment', 'mysql')).toBe('myschema.Appointment');
  });
});

describe('splitTableName', () => {
  it('SQL Server: no dot -> schemaName "dbo"', () => {
    expect(splitTableName('Appointment', 'sql')).toEqual({ schemaName: 'dbo', tableName: 'Appointment' });
  });
  it('PostgreSQL: no dot -> schemaName "public"', () => {
    expect(splitTableName('Appointment', 'postgres')).toEqual({ schemaName: 'public', tableName: 'Appointment' });
  });
  it('MySQL: no dot -> schemaName "" (this is the exact fix for the "dbo" fallback bug)', () => {
    expect(splitTableName('Appointment', 'mysql')).toEqual({ schemaName: '', tableName: 'Appointment' });
  });
  it('an already-qualified name is split on its own dot regardless of destType', () => {
    expect(splitTableName('myschema.Appointment', 'postgres')).toEqual({ schemaName: 'myschema', tableName: 'Appointment' });
  });
});

describe('reconcileTargetsForDestTypeSwitch', () => {
  const catalog = { Appointment: 'dbo.Appointment', Patient: 'dbo.Patient' };

  it('SQL Server -> PostgreSQL: an untouched default is re-qualified from dbo. to public.', () => {
    const targets = { Appointment: 'dbo.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'postgres');
    expect(next['Appointment']).toBe('public.Appointment');
  });

  it('SQL Server -> MySQL: an untouched default is re-qualified from dbo.X to bare X', () => {
    const targets = { Appointment: 'dbo.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'mysql');
    expect(next['Appointment']).toBe('Appointment');
  });

  it('PostgreSQL -> MySQL: an untouched default is re-qualified from public.X to bare X', () => {
    const targets = { Appointment: 'public.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'postgres', 'mysql');
    expect(next['Appointment']).toBe('Appointment');
  });

  it('MySQL -> PostgreSQL: an untouched default is re-qualified from bare X to public.X', () => {
    const targets = { Appointment: 'Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'mysql', 'postgres');
    expect(next['Appointment']).toBe('public.Appointment');
  });

  it('PostgreSQL -> SQL Server: an untouched default is re-qualified from public.X to dbo.X', () => {
    const targets = { Appointment: 'public.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'postgres', 'sql');
    expect(next['Appointment']).toBe('dbo.Appointment');
  });

  it('MySQL -> SQL Server: an untouched default is re-qualified from bare X to dbo.X', () => {
    const targets = { Appointment: 'Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'mysql', 'sql');
    expect(next['Appointment']).toBe('dbo.Appointment');
  });

  it('does NOT overwrite a manually renamed/created table, even though the type switched', () => {
    const targets = { Appointment: 'dbo.MyCustomAppointmentsTable' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'postgres');
    expect(next['Appointment']).toBe('dbo.MyCustomAppointmentsTable');
  });

  it('does NOT overwrite a value that happens to already be correct for the new type', () => {
    // e.g. the user already fixed it by hand, or "Select Existing" picked a real public.Appointment.
    const targets = { Appointment: 'public.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'postgres');
    expect(next['Appointment']).toBe('public.Appointment');
  });

  it('is a no-op when previousType === type', () => {
    const targets = { Appointment: 'dbo.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'sql');
    expect(next).toEqual(targets);
  });

  it('is a no-op for a switch into/out of a non-SQL-family type (e.g. Mongo)', () => {
    const targets = { Appointment: 'dbo.Appointment' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'mongo');
    expect(next['Appointment']).toBe('dbo.Appointment');
  });

  it('leaves a resource missing from the catalog map completely untouched', () => {
    const targets = { Unknown: 'dbo.Unknown' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'postgres');
    expect(next['Unknown']).toBe('dbo.Unknown');
  });

  it('leaves an empty/falsy target untouched', () => {
    const targets = { Appointment: '' };
    const next = reconcileTargetsForDestTypeSwitch(targets, catalog, 'sql', 'postgres');
    expect(next['Appointment']).toBe('');
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

describe('effectiveMappingValueType', () => {
  it('childJson row -> "Json", regardless of sources (matches serializeRowsFlat\'s StoreJson -> Json)', () => {
    const row: MappingRow = { resource: 'Practitioner', sources: [], mode: 'childJson', childNodeId: 'name', targetName: 'name', tableName: 'public.Practitioner' };
    expect(effectiveMappingValueType(row)).toBe('Json');
  });

  it('value row -> the primary source field\'s own valueType', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family', valueType: 'String' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'Family', tableName: 'public.Patient',
    };
    expect(effectiveMappingValueType(row)).toBe('String');
  });

  it('value row with no declared source valueType -> undefined (nothing to compare against)', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'Family', tableName: 'public.Patient',
    };
    expect(effectiveMappingValueType(row)).toBeUndefined();
  });
});

describe('checkColumnTypeCompatibility', () => {
  // The exact regression this exists for: Practitioner.name (a repeating/complex FHIR element) mapped as
  // "map this whole node as JSON" onto a PostgreSQL `character varying` column — this used to save
  // successfully from Map Fields and only fail later at Workflow Save with the backend's own copy of this
  // same check ("'name' is a character varying column (expects String), but this field is mapped as Json.").
  describe('regression: childJson onto a String-only column (PostgreSQL character varying)', () => {
    const childJsonRow: MappingRow = {
      resource: 'Practitioner', sources: [{ fhirPath: 'Practitioner.name', label: 'Name' }],
      mode: 'childJson', childNodeId: 'name', targetName: 'name', tableName: 'public.Practitioner',
    };

    it('is rejected — Map Fields Save must fail, not just Workflow Save', () => {
      const column = { dataType: 'character varying', mappingValueType: 'String' };
      const error = checkColumnTypeCompatibility(childJsonRow, column);
      expect(error).not.toBeNull();
      expect(error).toBe(
        '"name" on public.Practitioner is a character varying column (expects String), ' +
        'but "Name" is mapped as Json — pick a compatible source field or retarget to a Json-compatible column.',
      );
    });

    it('the same childJson mapping onto a column that DOES accept Json succeeds', () => {
      // Doesn't accidentally reject every childJson mapping — only ones the destination column can't hold.
      const jsonColumn = { dataType: 'jsonb', mappingValueType: 'Json' };
      expect(checkColumnTypeCompatibility(childJsonRow, jsonColumn)).toBeNull();
    });
  });

  it('normal value mapping with compatible types succeeds (unchanged from before)', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family', valueType: 'String' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'Family', tableName: 'public.Patient',
    };
    const column = { dataType: 'character varying', mappingValueType: 'String' };
    expect(checkColumnTypeCompatibility(row, column)).toBeNull();
  });

  it('normal value mapping with incompatible types fails exactly as before (no implicit widening)', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.birthDate', label: 'Birth Date', valueType: 'Integer' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'BirthYear', tableName: 'public.Patient',
    };
    const column = { dataType: 'numeric', mappingValueType: 'Decimal' };
    const error = checkColumnTypeCompatibility(row, column);
    expect(error).toBe(
      '"BirthYear" on public.Patient is a numeric column (expects Decimal), ' +
      'but "Birth Date" is mapped as Integer — pick a compatible source field or retarget to a Integer-compatible column.',
    );
  });

  it('MySQL: type compatibility check itself is destType-agnostic — same rule, different dataType wording', () => {
    const row: MappingRow = {
      resource: 'Practitioner', sources: [{ fhirPath: 'Practitioner.name', label: 'Name' }],
      mode: 'childJson', childNodeId: 'name', targetName: 'name', tableName: 'Practitioner',
    };
    const column = { dataType: 'varchar', mappingValueType: 'String' };
    expect(checkColumnTypeCompatibility(row, column)).not.toBeNull();
  });

  it('SQL Server: same rejection for a childJson-onto-varchar mismatch', () => {
    const row: MappingRow = {
      resource: 'Practitioner', sources: [{ fhirPath: 'Practitioner.name', label: 'Name' }],
      mode: 'childJson', childNodeId: 'name', targetName: 'name', tableName: 'dbo.Practitioner',
    };
    const column = { dataType: 'nvarchar', mappingValueType: 'String' };
    expect(checkColumnTypeCompatibility(row, column)).not.toBeNull();
  });

  // No schema probe in this codebase ever reports a column's own mappingValueType as literally "Json"
  // (every character type — including a real Postgres jsonb — collapses to "String"), so a column
  // intentionally used to hold JSON must not be rejected purely on that exact-match mismatch — while a
  // genuinely length-BOUNDED column can't hold it and must still fail.
  describe('regression: JSON source onto an unbounded (max-length) String column', () => {
    const childJsonRow: MappingRow = {
      resource: 'Observation', sources: [{ fhirPath: 'Observation.component', label: 'Component' }],
      mode: 'childJson', childNodeId: 'component', targetName: 'ComponentJson', tableName: 'dbo.Observation',
    };

    it('SQL Server nvarchar(max) (bare "nvarchar" dataType — what a live probe actually reports; maxLength: null from its -1) is allowed', () => {
      const column = { dataType: 'nvarchar', mappingValueType: 'String', maxLength: null };
      expect(checkColumnTypeCompatibility(childJsonRow, column)).toBeNull();
    });

    it('a length-bounded nvarchar(n) (a real maxLength) still fails — genuine truncation risk', () => {
      const column = { dataType: 'nvarchar', mappingValueType: 'String', maxLength: 200 };
      expect(checkColumnTypeCompatibility(childJsonRow, column)).not.toBeNull();
    });

    it('a rule declaring Json output onto the same unbounded column is likewise allowed', () => {
      const column = { dataType: 'nvarchar', mappingValueType: 'String', maxLength: null };
      expect(checkColumnTypeCompatibility(childJsonRow, column, 'Json')).toBeNull();
    });

    // MySQL's information_schema NEVER reports a null character_maximum_length for TEXT/MEDIUMTEXT/
    // LONGTEXT — always a real (if huge) number, since capacity is fixed by the type keyword itself, not
    // an independently configurable length the way varchar(n) is. maxLength === null alone would miss
    // these entirely — they must be recognized by data type NAME instead (isJsonSafeForColumn). Jasmine
    // (this suite's actual framework — no it.each here, that's a Jest-only API) has no parameterized-test
    // helper, so this is a plain loop generating one `it` per case instead.
    const unboundedByNameCases: Array<[dataType: string, maxLength: number]> = [
      ['text', 65535],
      ['mediumtext', 16777215],
      ['longtext', 4294967295],
      ['ntext', 1073741823], // SQL Server's own legacy unbounded unicode type — same non-null quirk.
    ];
    for (const [dataType, maxLength] of unboundedByNameCases) {
      it(`MySQL/SQL-Server-style unbounded-by-name "${dataType}" column (maxLength: ${maxLength}, not null) is allowed`, () => {
        const column = { dataType, mappingValueType: 'String', maxLength };
        expect(checkColumnTypeCompatibility(childJsonRow, column)).toBeNull();
      });
    }

    // TINYTEXT (255 chars) is genuinely too small for arbitrary JSON — must NOT be swept up by the same
    // by-name exception just because it shares the "*text" naming family as its larger siblings.
    it('MySQL TINYTEXT still fails — genuinely too small for arbitrary JSON', () => {
      const column = { dataType: 'tinytext', mappingValueType: 'String', maxLength: 255 };
      expect(checkColumnTypeCompatibility(childJsonRow, column)).not.toBeNull();
    });
  });

  it('is a no-op when the column has no mappingValueType metadata yet (nothing to compare against)', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family', valueType: 'String' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'Family', tableName: 'public.Patient',
    };
    expect(checkColumnTypeCompatibility(row, undefined)).toBeNull();
    expect(checkColumnTypeCompatibility(row, { dataType: 'text' })).toBeNull();
  });

  it('is a no-op when the row has no known effective type yet (still-empty/unfinished row)', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'Family', tableName: 'public.Patient',
    };
    const column = { dataType: 'character varying', mappingValueType: 'String' };
    expect(checkColumnTypeCompatibility(row, column)).toBeNull();
  });

  it('is case-insensitive on both the effective type and the column type (same as before)', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family', valueType: 'string' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'Family', tableName: 'public.Patient',
    };
    const column = { dataType: 'character varying', mappingValueType: 'STRING' };
    expect(checkColumnTypeCompatibility(row, column)).toBeNull();
  });
});
