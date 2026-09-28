import {
  migrateLegacyRow, serializeRowsFlat, resolveArrayPolicy, isApproximated,
  MappingRow, LegacyMappingRow,
  qualifyTableName, defaultSchemaFor, splitTableName, reconcileTargetsForDestTypeSwitch,
  effectiveMappingValueType, checkColumnTypeCompatibility,
  supportsJsonWriteMode, resolveJsonWriteMode, formatWithJsonWriteMode, jsonWriteModeFromFormat,
  defaultInstanceType, wholeNodeInstanceIndex, wholeNodeJsonPath,
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
  it('emits a joinedFields entry carrying EVERY source for a multi-source row', () => {
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
    // EVERY source is carried, with the mode+delimiter marker JsonMappingEngine.ResolveJoinedFields reads.
    // Sending only sources[0] is what silently dropped the family name. The "|"-joined JsonPath itself is
    // assembled downstream (workflow-build-assembler-v2), which can derive a path for a source the catalog
    // never gave a jsonPath — so the join survives a source that has only a fhirPath.
    expect(flat[0].joinSources).toEqual([
      { path: 'Patient.name.given', jsonPath: '$.name[*].given[*]', arrays: ['name'] },
      { path: 'Patient.name.family', jsonPath: '$.name[*].family', arrays: ['name'] },
    ]);
    expect(flat[0].format).toBe('joinedFields;delimiter= ');
    expect(flat[0].approximated).toBeFalse();
  });

  it('carries a joined source that has no catalog jsonPath, rather than degrading to the primary', () => {
    // The real shape of a canvas-dragged row: fhirPath + label + arrays, no jsonPath. Requiring one on every
    // source made this row fall back to sources[0] alone while still stamping joinedFields, so the engine
    // joined a single sub-path and the column got just the first field.
    const rows: MappingRow[] = [{
      resource: 'Patient',
      sources: [
        { fhirPath: 'Patient.name.family', label: 'family', arrays: ['name'] },
        { fhirPath: 'Patient.name.given', label: 'given', arrays: ['name'] },
      ],
      mode: 'value', delimiter: ', ', instance: { type: 'first' },
      targetName: 'FULLNAME', tableName: 'dbo.Patient',
    }];
    const flat = serializeRowsFlat(rows, { Patient: 'dbo.Patient' });
    expect(flat[0].joinSources).toEqual([
      { path: 'Patient.name.family', arrays: ['name'] },
      { path: 'Patient.name.given', arrays: ['name'] },
    ]);
    expect(flat[0].format).toBe('joinedFields;delimiter=, ');
  });

  it('emits no joinSources for a single-source row', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient',
      sources: [{ fhirPath: 'Patient.gender', label: 'Gender', jsonPath: '$.gender' }],
      mode: 'value', instance: { type: 'first' },
      targetName: 'Gender', tableName: 'dbo.Patient',
    }];
    expect(serializeRowsFlat(rows, { Patient: 'dbo.Patient' })[0].joinSources).toBeUndefined();
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

  // MongoDB "store this JSON as a real sub-document" — carried to the backend on `format` (read there by
  // MappingFieldFormat.ReadJsonWriteMode), which serializeRowsFlat emits ONLY for this, so every other row
  // keeps producing exactly the payload it always has.
  describe('jsonWriteMode', () => {
    function jsonRow(overrides: Partial<MappingRow> = {}): MappingRow {
      return {
        resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient',
        targetName: 'Patient', tableName: 'patients_local',
        ...overrides,
      };
    }

    it('omits format entirely when the row never made the choice (every pre-existing mapping)', () => {
      expect(serializeRowsFlat([jsonRow()], {})[0].format).toBeUndefined();
    });

    it("omits format for an explicit 'string' choice too — that IS the default behaviour", () => {
      expect(serializeRowsFlat([jsonRow({ jsonWriteMode: 'string' })], {})[0].format).toBeUndefined();
    });

    it("emits the json=document marker for a 'document' choice", () => {
      expect(serializeRowsFlat([jsonRow({ jsonWriteMode: 'document' })], {})[0].format)
        .toBe('json=document');
    });

    it('preserves any other markers already on the row format', () => {
      const flat = serializeRowsFlat(
        [jsonRow({ jsonWriteMode: 'document', format: 'wholeNodeAsJson' })], {});
      expect(flat[0].format).toBe('wholeNodeAsJson;json=document');
    });

    it('never emits the marker for an ordinary non-Json row', () => {
      const valueRow: MappingRow = {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.gender', label: 'Gender', valueType: 'String' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'Gender', tableName: 'patients_local',
      };
      expect(serializeRowsFlat([valueRow], {})[0].format).toBeUndefined();
    });

    it('supportsJsonWriteMode follows the row\'s effective ValueType', () => {
      expect(supportsJsonWriteMode(jsonRow())).toBeTrue();
      expect(supportsJsonWriteMode({
        resource: 'Patient', sources: [{ fhirPath: 'Patient.gender', label: 'Gender', valueType: 'String' }],
        mode: 'value', targetName: 'Gender', tableName: 'patients_local',
      })).toBeFalse();
    });

    // A row rebuilt from the Mapping JSON summary comes back with no source valueType (sourceRefFromPath
    // doesn't round-trip it), so the choice it explicitly carries has to be enough on its own — otherwise
    // reopening and re-saving silently drops it.
    it('honours an explicitly-carried mode even when the source type is unknown', () => {
      const restored: MappingRow = {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.extension', label: 'extension' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'Extension', tableName: 'patients_local',
        jsonWriteMode: 'document',
      };
      expect(supportsJsonWriteMode(restored)).toBeTrue();
      expect(serializeRowsFlat([restored], {})[0].format).toBe('json=document');
    });

    it('resolveJsonWriteMode defaults to "string"', () => {
      expect(resolveJsonWriteMode(jsonRow())).toBe('string');
      expect(resolveJsonWriteMode(jsonRow({ jsonWriteMode: 'document' }))).toBe('document');
    });

    // Read back out of a saved MappingField's `format` by the "Select Existing Profile" load path
    // (destination-wizard.component.ts's _applyExistingProfile) — the third route into MappingRow[], after
    // dest_mappings_v2 and the Mapping JSON summary. `format` is a ';'-separated marker BAG, so this is a
    // substring test, matching the backend's MappingFieldFormat.ReadJsonWriteMode: an equality check against
    // 'json=document' would miss every compound value, which is the shape formatWithJsonWriteMode actually
    // produces whenever the field already carried a column-mode marker.
    describe('jsonWriteModeFromFormat', () => {
      it('reads the marker on its own and inside a marker bag, case-insensitively', () => {
        expect(jsonWriteModeFromFormat('json=document')).toBe('document');
        expect(jsonWriteModeFromFormat('wholeNodeAsJson;json=document')).toBe('document');
        expect(jsonWriteModeFromFormat('WholeNodeAsJson;JSON=DOCUMENT')).toBe('document');
      });

      it("is 'string' for every format that doesn't carry it", () => {
        expect(jsonWriteModeFromFormat(null)).toBe('string');
        expect(jsonWriteModeFromFormat(undefined)).toBe('string');
        expect(jsonWriteModeFromFormat('')).toBe('string');
        expect(jsonWriteModeFromFormat('wholeNodeAsJson')).toBe('string');
        expect(jsonWriteModeFromFormat('wholeNodeAsJson;json=string')).toBe('string');
        expect(jsonWriteModeFromFormat('directField;aggregate=csv')).toBe('string');
      });

      // The regression: reapplying a saved profile used to copy `format` but not derive jsonWriteMode, so a
      // field saved as a document came back 'string' — the popover showed "JSON string" and the next save
      // dropped the marker outright. Rebuilding the row the way _applyExistingProfile does must survive it.
      it('lets a reapplied profile row keep writing a document on the next save', () => {
        const fromProfile = (format: string | null): MappingRow => {
          const jsonWriteMode = jsonWriteModeFromFormat(format);
          return {
            resource: 'Patient',
            sources: [{ fhirPath: 'Patient', label: 'Patient', jsonPath: '$', valueType: 'Json' }],
            mode: 'value', instance: { type: 'first' },
            targetName: 'Patient', tableName: 'patients_local',
            format,
            ...(jsonWriteMode === 'document' ? { jsonWriteMode } : {}),
          };
        };

        const row = fromProfile('json=document');
        expect(resolveJsonWriteMode(row)).toBe('document');
        expect(serializeRowsFlat([row], { Patient: 'patients_local' })[0].format).toBe('json=document');
      });

      // Why 'document' is spread in rather than the mode always being assigned: supportsJsonWriteMode() is
      // true for any row carrying an explicit mode, so stamping 'string' on every reapplied field would
      // surface the Mongo-only dropdown on plain String columns that have no such choice to make.
      it('leaves a non-Json column with no mode at all, keeping the option hidden for it', () => {
        expect(jsonWriteModeFromFormat(null)).toBe('string');
        const plain: MappingRow = {
          resource: 'Patient',
          sources: [{ fhirPath: 'Patient.gender', label: 'Gender', valueType: 'String' }],
          mode: 'value', instance: { type: 'first' },
          targetName: 'Gender', tableName: 'patients_local', format: null,
        };
        expect(plain.jsonWriteMode).toBeUndefined();
        expect(supportsJsonWriteMode(plain)).toBeFalse();
      });
    });

    // `format` segments carry arbitrary text — JsonMappingEngine.ParseDelimiter takes everything after the
    // first '=' as a joined field's delimiter — so the marker is matched as a whole ';'-separated segment,
    // never as a substring of the joined value (mirrors the backend's MappingFieldFormat.ReadJsonWriteMode).
    it('does not mistake the marker embedded in another segment for the real thing', () => {
      expect(jsonWriteModeFromFormat('joinedFields;delimiter=json=document')).toBe('string');
      expect(jsonWriteModeFromFormat('json=documentary')).toBe('string');
      expect(jsonWriteModeFromFormat('notjson=document')).toBe('string');
      // ...while a real segment still reads, whitespace and casing included.
      expect(jsonWriteModeFromFormat('directField; json=document ')).toBe('document');
    });

    // A join's value is string.Join(delimiter, pieces) on the backend (JsonMappingEngine.ResolveJoinedFields):
    // a delimited string by construction, never a JSON document, whatever its first source's type claims. So
    // the choice is never offered for one — which is also why toSummaryColumn's joinedFields branch has no
    // jsonWriteMode to persist.
    it('offers nothing for a join, even one whose primary source is Json-typed', () => {
      const join: MappingRow = {
        resource: 'Patient',
        sources: [
          { fhirPath: 'Patient.name.given', label: 'Given', valueType: 'Json' },
          { fhirPath: 'Patient.name.family', label: 'Family', valueType: 'String' },
        ],
        mode: 'value', instance: { type: 'first' }, delimiter: ', ',
        targetName: 'Name', tableName: 'patients_local', jsonWriteMode: 'document',
      };
      expect(supportsJsonWriteMode(join)).toBeFalse();
      // A join DOES emit a format — the "joinedFields" mode prefix, without which JsonMappingEngine resolves
      // the "|"-delimited jsonPath as one literal path and the column goes silently NULL. So the assertion
      // is that no json= marker rides along with it, not that there is no format at all; "format is
      // undefined" was only ever a proxy for that, accurate while a join emitted none.
      expect(serializeRowsFlat([join], {})[0].format).toBe('joinedFields;delimiter=, ');
      expect(serializeRowsFlat([join], {})[0].format).not.toContain('json=');
    });

    // The marker and the declared ValueType have to agree: the backend honours the marker only on a
    // ValueType=Json field (MappedMongoDestinationWriter.ResolveDocumentJsonColumns), and JsonMappingEngine
    // only hands the value through as raw JSON text for that type. A row rebuilt from the Mapping JSON
    // summary has lost its source valueType, so without this the assembler's path-guessed 'String' would win
    // and the write would silently downgrade to escaped text while the UI still showed "JSON document".
    it('declares Json whenever it emits the marker, even with no source valueType', () => {
      const lostType: MappingRow = {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.extension', label: 'extension' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'Extension', tableName: 'patients_local', jsonWriteMode: 'document',
      };
      const flat = serializeRowsFlat([lostType], {})[0];
      expect(flat.format).toBe('json=document');
      expect(flat.valueType).toBe('Json');
    });

    it("leaves an unmarked row's own valueType alone", () => {
      const plain: MappingRow = {
        resource: 'Patient',
        sources: [{ fhirPath: 'Patient.gender', label: 'Gender', valueType: 'String' }],
        mode: 'value', instance: { type: 'first' },
        targetName: 'Gender', tableName: 'patients_local',
      };
      const flat = serializeRowsFlat([plain], {})[0];
      expect(flat.valueType).toBe('String');
      expect(flat.format).toBeUndefined();
    });

    it('formatWithJsonWriteMode replaces a previously-stored json= marker rather than stacking one', () => {
      expect(formatWithJsonWriteMode('wholeNodeAsJson;json=document', 'string')).toBe('wholeNodeAsJson');
      expect(formatWithJsonWriteMode('json=document', 'string')).toBeUndefined();
      expect(formatWithJsonWriteMode('json=document', 'document')).toBe('json=document');
    });
  });

  // Which instance a whole-node mapping reads travels as an index in the JsonPath, not in the ArrayPolicy
  // enum (which has no "the nth one, as JSON" member) -- see wholeNodeJsonPath.
  describe('childJson instance selection', () => {
    function wholeNodeRow(instance?: MappingRow['instance']): MappingRow {
      return {
        resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.name',
        instance, targetName: 'NameJson', tableName: 'dbo.Patient',
      };
    }

    it('emits no explicit jsonPath when unset, so the whole node is still read as before', () => {
      const flat = serializeRowsFlat([wholeNodeRow()], {});
      expect(flat[0].jsonPath).toBeUndefined();
      expect(flat[0].path).toBe('Patient.name');
    });

    it('"all records" also reads the whole node - no index', () => {
      expect(serializeRowsFlat([wholeNodeRow({ type: 'all' })], {})[0].jsonPath).toBeUndefined();
    });

    it('"first instance" indexes the node, keeping StoreJson and Json', () => {
      const flat = serializeRowsFlat([wholeNodeRow({ type: 'first' })], {});
      expect(flat[0].jsonPath).toBe('$.name[0]');
      expect(flat[0].arrayPolicy).toBe('StoreJson');
      expect(flat[0].valueType).toBe('Json');
      expect(flat[0].approximated).toBeFalse();
    });

    it('"nth instance" is exact - the 1-based UI number becomes a 0-based index', () => {
      const flat = serializeRowsFlat([wholeNodeRow({ type: 'nth', n: 3 })], {});
      expect(flat[0].jsonPath).toBe('$.name[2]');
      expect(flat[0].approximated).toBeFalse();
    });

    it('"match criteria" becomes a filter on the node, not a position', () => {
      const flat = serializeRowsFlat([wholeNodeRow({ type: 'criteria', field: 'use', op: '=', value: 'official' })], {});
      expect(flat[0].jsonPath).toBe('$.name[?use=official]');
      expect(flat[0].approximated).toBeFalse();
    });

    it('maps each criteria operator to its wire form', () => {
      const of = (op: 'contains' | '!=' | '=') =>
        serializeRowsFlat([wholeNodeRow({ type: 'criteria', field: 'use', op, value: 'official' })], {})[0].jsonPath;
      expect(of('=')).toBe('$.name[?use=official]');
      expect(of('!=')).toBe('$.name[?use!=official]');
      expect(of('contains')).toBe('$.name[?use~official]');
    });

    it('selects nothing rather than everything while the criteria names no field yet', () => {
      const flat = serializeRowsFlat([wholeNodeRow({ type: 'criteria', field: '', op: '=', value: 'official' })], {});
      expect(flat[0].jsonPath).toBeUndefined();
    });

    it('never indexes the resource root - a resource object is not a repeating element', () => {
      const wholePayload: MappingRow = {
        resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient',
        instance: { type: 'first' }, targetName: 'content', tableName: 'dbo.Patient',
      };
      expect(serializeRowsFlat([wholePayload], {})[0].jsonPath).toBeUndefined();
    });
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

  // Regression: mapping the whole payload as JSON onto a HAPI-style `content text NOT NULL` column was
  // unsaveable — the save gate (CreateMappingProfileRequestValidator) rejects a NOT NULL column whose field
  // is neither required nor defaulted, and this wizard has no per-row "required" toggle to satisfy it with.
  describe('isRequired', () => {
    const sqlTables: DestinationTable[] = [{
      schemaName: 'public', tableName: 'Patient', fullName: 'public.Patient',
      columns: [
        { name: 'content', dataType: 'text', mappingValueType: 'String', isNullable: false, maxLength: null },
        { name: 'gender', dataType: 'text', mappingValueType: 'String', isNullable: true, maxLength: null },
      ],
    }];

    function wholePayloadRow(): MappingRow {
      return {
        resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient',
        targetName: 'content', tableName: 'public.Patient',
      };
    }

    it('marks a row targeting a NOT NULL column as required', () => {
      const flat = serializeRowsFlat([wholePayloadRow()], { Patient: 'public.Patient' }, sqlTables);
      expect(flat[0].isRequired).toBeTrue();
    });

    it('omits isRequired for a nullable column', () => {
      const row: MappingRow = {
        resource: 'Patient', sources: [{ fhirPath: 'Patient.gender', label: 'Gender' }],
        mode: 'value', instance: { type: 'first' }, targetName: 'gender', tableName: 'public.Patient',
      };
      const flat = serializeRowsFlat([row], { Patient: 'public.Patient' }, sqlTables);
      expect(flat[0].isRequired).toBeUndefined();
    });

    it('omits isRequired when the column has no known live schema', () => {
      const flat = serializeRowsFlat([wholePayloadRow()], { Patient: 'public.Patient' });
      expect(flat[0].isRequired).toBeUndefined();
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

    it('a root-table csv-aggregate field carries the aggregate=csv Format marker JsonMappingEngine reads', () => {
      const flat = serializeRowsFlat(
        [{ resource: 'Patient', sources: [{ fhirPath: 'Patient.telecom.use', label: 'Use', arrays: ['telecom'] }], mode: 'value', instance: { type: 'all', aggregate: 'csv' }, targetName: 'telecom', tableName: 'dbo.Patient' }],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('FirstItem');
      expect(flat[0].format).toBe('directField;aggregate=csv');
    });

    it('a root-table Nth-instance field converts its 1-based instance.n to the 0-based index=N Format marker JsonMappingEngine reads', () => {
      const flat = serializeRowsFlat(
        // n=2 is the user-facing "2nd instance" — the wire Format marker is the 0-based array index (1).
        [{ resource: 'Patient', sources: [{ fhirPath: 'Patient.telecom.use', label: 'Use', arrays: ['telecom'] }], mode: 'value', instance: { type: 'nth', n: 2 }, targetName: 'telecom', tableName: 'dbo.Patient' }],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('FirstItem');
      expect(flat[0].format).toBe('directField;index=1');
    });

    it('suppresses the csv-aggregate Format marker once the field is fanned out to its own child-table row per item', () => {
      // Collapsing every occurrence into one delimited string only makes sense on the parent row; once
      // isChildTable overrides the policy to SeparateDestination, each occurrence already becomes its own
      // child-table row, so the marker must not survive onto it (see serializeRowsFlat's own `format` calc).
      const flat = serializeRowsFlat(
        [childRow({ sources: [{ fhirPath: 'Patient.name.given', label: 'Given', arrays: ['name'] }], instance: { type: 'all', aggregate: 'csv' } })],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('SeparateDestination');
      expect(flat[0].format).toBeUndefined();
    });

    it('a root-table criteria field emits CorrelateByCode plus the bare sibling field name — the absolute CorrelationCodeJsonPath is derived downstream, in workflow-build-assembler-v2.service.ts, off this row\'s own resolved jsonPath (frequently unavailable at this layer; see MappingSourceRef.jsonPath)', () => {
      const flat = serializeRowsFlat(
        [{
          resource: 'Patient',
          // Deliberately NO catalog jsonPath on the source — this is the common case in the live app
          // (MappingSourceRef.jsonPath is only "when present"), and correlationSiblingField must still be
          // emitted; only the FINAL absolute path build depends on a jsonPath, and that now happens later.
          sources: [{ fhirPath: 'Patient.telecom.value', label: 'Value', arrays: ['telecom'] }],
          mode: 'value', instance: { type: 'criteria', field: 'use', op: '=', value: 'mobile' },
          targetName: 'telecom', tableName: 'dbo.Patient',
        }],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('CorrelateByCode');
      expect(flat[0].correlationSiblingField).toBe('use');
      expect(flat[0].correlationCodeValue).toBe('mobile');
      expect(flat[0].correlationOperator).toBe('Equals');
    });

    it('suppresses the correlation fields once a criteria field is fanned out to its own child-table row per item', () => {
      const flat = serializeRowsFlat(
        [childRow({
          sources: [{ fhirPath: 'Patient.name.given', label: 'Given', arrays: ['name'] }],
          instance: { type: 'criteria', field: 'use', op: '=', value: 'official' },
        })],
        targetByResource, [], childRelations,
      );
      expect(flat[0].arrayPolicy).toBe('SeparateDestination');
      expect(flat[0].correlationSiblingField).toBeUndefined();
      expect(flat[0].correlationCodeValue).toBeUndefined();
      expect(flat[0].correlationOperator).toBeUndefined();
    });
  });

  describe('mode: default', () => {
    it('a literal ("@default") row emits the token as jsonPath and the literal as defaultValue', () => {
      const rows: MappingRow[] = [{
        resource: 'Patient', sources: [], mode: 'default',
        defaultToken: '@default', defaultValue: 'Patient', defaultValueType: 'String',
        targetName: 'ClientType', tableName: 'dbo.Patient',
      }];
      const flat = serializeRowsFlat(rows, { Patient: 'dbo.Patient' });
      expect(flat[0].jsonPath).toBe('@default');
      expect(flat[0].defaultValue).toBe('Patient');
      expect(flat[0].valueType).toBe('String');
    });

    it('a runtime-token ("@now") row emits the token as jsonPath with no defaultValue', () => {
      const rows: MappingRow[] = [{
        resource: 'Patient', sources: [], mode: 'default',
        defaultToken: '@now', defaultValue: null, defaultValueType: 'DateTime',
        targetName: 'WrittenOnUtc', tableName: 'dbo.Patient',
      }];
      const flat = serializeRowsFlat(rows, { Patient: 'dbo.Patient' });
      expect(flat[0].jsonPath).toBe('@now');
      expect(flat[0].defaultValue).toBeUndefined();
      expect(flat[0].valueType).toBe('DateTime');
    });

    // Regression guard for the bug this shape exists to prevent: a 'default' row with no jsonPath override
    // used to fall through to the 'value' branch with an empty sources[] — path/jsonPath both ended up
    // empty, which workflow-build-assembler.service.ts's toJsonPath('') resolves to "$" (the WHOLE source
    // document), silently writing the raw resource JSON into the column on every pipeline run.
    it('never falls through to the plain directField shape (path/jsonPath must never be empty)', () => {
      const rows: MappingRow[] = [{
        resource: 'Patient', sources: [], mode: 'default',
        defaultToken: '@runId', defaultValue: null, defaultValueType: 'String',
        targetName: 'PipelineRunId', tableName: 'dbo.Patient',
      }];
      const flat = serializeRowsFlat(rows, { Patient: 'dbo.Patient' });
      expect(flat[0].path).toBeTruthy();
      expect(flat[0].jsonPath).toBeTruthy();
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

  it('childJson mode stays StoreJson for every exact instance selection', () => {
    const exact: MappingRow['instance'][] = [{ type: 'all' }, { type: 'first' }, { type: 'nth', n: 2 }];
    for (const instance of exact) {
      expect(resolveArrayPolicy(row({ mode: 'childJson', sources: [], childNodeId: 'name', instance })))
        .toEqual({ arrayPolicy: 'StoreJson', approximated: false });
    }
  });

  it('childJson mode with "match criteria" -> StoreJson, exact (the filter rides in the JsonPath)', () => {
    expect(resolveArrayPolicy(row({ mode: 'childJson', sources: [], childNodeId: 'name', instance: { type: 'criteria' } })))
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

  it('single source, instance "all"/aggregate csv -> FirstItem, NOT approximated, carries the aggregate=csv Format marker', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'all', aggregate: 'csv' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false, format: 'directField;aggregate=csv' });
  });

  it('single source, instance "nth", 1-based n=2 (the second instance) -> FirstItem, NOT approximated, carries the 0-based index=1 Format marker', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'nth', n: 2 } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false, format: 'directField;index=1' });
  });

  it('instance "nth" with no explicit n defaults to n=1 (the first instance, index=0)', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'nth' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false, format: 'directField;index=0' });
  });

  it('instance "nth", n=1 (the first instance) -> index=0, identical to plain "First"', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'nth', n: 1 } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false, format: 'directField;index=0' });
  });

  describe('instance "criteria"', () => {
    it('op "=" -> CorrelateByCode, NOT approximated, hands over the bare sibling field name (no catalog jsonPath needed at this layer — see correlationSiblingField\'s own doc comment) and correlationOperator "Equals"', () => {
      expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', op: '=', value: 'mobile' } })))
        .toEqual({
          arrayPolicy: 'CorrelateByCode', approximated: false,
          correlationSiblingField: 'use', correlationCodeValue: 'mobile', correlationOperator: 'Equals',
        });
    });

    it('op omitted entirely (onInstanceTypeChange never seeds one; the "equals" shown is only the <select>\'s own display default) -> still resolves as "Equals", NOT approximated', () => {
      // Regression guard for the exact bug reported live: switching "Which instance?" to "Match criteria"
      // and typing straight into field/value, without ever re-selecting "equals" in the op dropdown (already
      // showing it), left the real instance.op undefined — silently falling back to the approximation for
      // what looked, on screen, like a fully configured "equals" criteria.
      expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', value: 'mobile' } })))
        .toEqual({
          arrayPolicy: 'CorrelateByCode', approximated: false,
          correlationSiblingField: 'use', correlationCodeValue: 'mobile', correlationOperator: 'Equals',
        });
    });

    it('op "contains" -> CorrelateByCode, NOT approximated, correlationOperator "Contains"', () => {
      expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', op: 'contains', value: 'mob' } })))
        .toEqual({
          arrayPolicy: 'CorrelateByCode', approximated: false,
          correlationSiblingField: 'use', correlationCodeValue: 'mob', correlationOperator: 'Contains',
        });
    });

    it('op "!=" -> CorrelateByCode, NOT approximated, correlationOperator "NotEquals"', () => {
      expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', op: '!=', value: 'home' } })))
        .toEqual({
          arrayPolicy: 'CorrelateByCode', approximated: false,
          correlationSiblingField: 'use', correlationCodeValue: 'home', correlationOperator: 'NotEquals',
        });
    });

    it('a criteria still being typed (no field yet) -> FirstItem, approximated', () => {
      expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: '', op: '=', value: 'mobile' } })))
        .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
    });

    it('a criteria still being typed (no value yet) -> FirstItem, approximated', () => {
      expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', op: '=', value: '' } })))
        .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
    });

    it('a source with no repeating ancestor at all -> FirstItem, approximated (no array to correlate within)', () => {
      expect(resolveArrayPolicy(row({
        sources: [scalarSource],
        instance: { type: 'criteria', field: 'use', op: '=', value: 'mobile' },
      }))).toEqual({ arrayPolicy: 'FirstItem', approximated: true });
    });
  });

  it('joined sources (>1) -> joinedFields format, not approximated', () => {
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'first' },
      delimiter: ', ',
    });
    expect(resolveArrayPolicy(joined))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false, format: 'joinedFields;delimiter=, ' });
  });

  it('a joined row with an INCOMPLETE criteria still reports itself as approximated', () => {
    // The join is exact, but the instance selection layered on it is not — hard-coding approximated:false
    // for every joined row swallowed that, hiding the "Preview only" banner which is the only signal that
    // the criteria the user half-typed is not actually running.
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'criteria', field: 'use', op: '=', value: '' },
      delimiter: ', ',
    });
    expect(resolveArrayPolicy(joined))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true, format: 'joinedFields;delimiter=, ' });
  });

  it('a joined row with a COMPLETE criteria keeps CorrelateByCode and is exact', () => {
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'criteria', field: 'use', op: '=', value: 'official' },
      delimiter: ', ',
    });
    expect(resolveArrayPolicy(joined)).toEqual({
      arrayPolicy: 'CorrelateByCode',
      approximated: false,
      correlationSiblingField: 'use',
      correlationCodeValue: 'official',
      correlationOperator: 'Equals',
      format: 'joinedFields;delimiter=, ',
    });
  });

  it('joined sources keep the instance marker alongside the joinedFields prefix', () => {
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'all', aggregate: 'csv' },
      delimiter: ', ',
    });
    expect(resolveArrayPolicy(joined)).toEqual({
      arrayPolicy: 'FirstItem', approximated: false,
      format: 'joinedFields;delimiter=, ;aggregate=csv',
    });
  });

  it('strips delimiter characters that would collide with the Format marker syntax', () => {
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'first' },
      delimiter: '; =',
    });
    expect(resolveArrayPolicy(joined).format).toBe('joinedFields;delimiter= ');
  });

  it('absent instance defaults to "first" semantics', () => {
    expect(resolveArrayPolicy(row({ instance: undefined })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false });
  });

  it('default mode -> Scalar, not approximated, regardless of instance', () => {
    expect(resolveArrayPolicy(row({ mode: 'default', sources: [], defaultToken: '@default', defaultValue: 'Patient' })))
      .toEqual({ arrayPolicy: 'Scalar', approximated: false });
  });
});

describe('isApproximated', () => {
  it('mirrors resolveArrayPolicy().approximated', () => {
    const exact: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Patient ID' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'SourcePatientId', tableName: 'dbo.Patient',
    };
    // 'nth' and a fully-configured 'criteria' (any op) are no longer approximations of their OWN accord
    // (see the resolveArrayPolicy suite) — but `exact`'s own source has no array ancestors at all
    // (Patient.id is scalar), so 'criteria' here still falls back: nothing to correlate against regardless
    // of how complete the criteria itself is.
    const approx: MappingRow = { ...exact, instance: { type: 'criteria', field: 'use', op: '=', value: 'official' } };
    expect(isApproximated(exact)).toBeFalse();
    expect(isApproximated(approx)).toBeTrue();
  });
});

describe('effectiveMappingValueType', () => {
  it('default row -> its own declared defaultValueType, not derived from any source field', () => {
    const row: MappingRow = {
      resource: 'Patient', sources: [], mode: 'default',
      defaultToken: '@now', defaultValue: null, defaultValueType: 'DateTime',
      targetName: 'WrittenOnUtc', tableName: 'dbo.Patient',
    };
    expect(effectiveMappingValueType(row)).toBe('DateTime');
  });

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

    describe('the remedy it names', () => {
      // Quantity/Range Assembly's output type is fixed by the node (TransformNodeTypeDefaults) — the rule
      // popover renders no control for it, so telling its author to "fix the rule's Expected output type"
      // sends them hunting for something that isn't there. Only NumberCast/DateTimeFormat have that control.
      const bounded = { dataType: 'character varying', mappingValueType: 'String', maxLength: 255 };

      it('points a fixed-output node at the column, not at a control it does not have', () => {
        const error = checkColumnTypeCompatibility(childJsonRow, bounded, 'Json', 'QuantityRangeAssembly');
        expect(error).toContain('QuantityRangeAssembly always does');
        expect(error).toContain('text column');
        expect(error).not.toContain("fix the rule's Expected output type");
      });

      it('still offers the output-type fix for a node that really exposes one', () => {
        const error = checkColumnTypeCompatibility(childJsonRow, bounded, 'Json', 'NumberCast');
        expect(error).toContain("fix the rule's Expected output type");
      });

      it('keeps the generic wording when the node type is unknown', () => {
        const error = checkColumnTypeCompatibility(childJsonRow, bounded, 'Json');
        expect(error).toContain("fix the rule's Expected output type");
      });

      it('only mentions text columns for a Json mismatch', () => {
        const intColumn = { dataType: 'integer', mappingValueType: 'Integer', maxLength: null };
        const error = checkColumnTypeCompatibility(childJsonRow, intColumn, 'Date', 'CodeableConceptBuilder');
        expect(error).toContain('CodeableConceptBuilder always does');
        expect(error).not.toContain('text column');
      });

      it('treats DateMathAge as overridable — its operation select decides Date vs Integer', () => {
        // resolveExpectedValueType derives DateMathAge's output from `operation` ("add"/"shift" -> Date,
        // "age" -> Integer), so "it always outputs Date" is false and sends the author to ALTER TABLE when
        // a dropdown on the rule already open is the real fix.
        const intColumn = { dataType: 'integer', mappingValueType: 'Integer', maxLength: null };
        const error = checkColumnTypeCompatibility(childJsonRow, intColumn, 'Date', 'DateMathAge');
        expect(error).toContain("fix the rule's Expected output type");
        expect(error).not.toContain('always does');
      });
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

describe('whole-node instance helpers', () => {
  function wholeNodeRow(instance?: MappingRow['instance']): MappingRow {
    return {
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.name',
      instance, targetName: 'NameJson', tableName: 'dbo.Patient',
    };
  }

  const valueRow: MappingRow = {
    resource: 'Patient', sources: [{ fhirPath: 'Patient.name.family', label: 'Family' }],
    mode: 'value', targetName: 'Family', tableName: 'dbo.Patient',
  };

  it('an unset instance means "the whole node" for a childJson row, "first" for a value row', () => {
    expect(defaultInstanceType(wholeNodeRow())).toBe('all');
    expect(defaultInstanceType(valueRow)).toBe('first');
  });

  it('maps each POSITIONAL instance type to the index it reads, or null', () => {
    expect(wholeNodeInstanceIndex(undefined)).toBeNull();
    expect(wholeNodeInstanceIndex({ type: 'all' })).toBeNull();
    expect(wholeNodeInstanceIndex({ type: 'first' })).toBe(0);
    expect(wholeNodeInstanceIndex({ type: 'nth', n: 4 })).toBe(3);
    // Criteria is not positional — it resolves to a filter instead (instanceCriteriaPredicate).
    expect(wholeNodeInstanceIndex({ type: 'criteria' })).toBeNull();
  });

  it('clamps a nonsensical instance number rather than emitting a negative index', () => {
    expect(wholeNodeInstanceIndex({ type: 'nth', n: 0 })).toBe(0);
    expect(wholeNodeInstanceIndex({ type: 'nth', n: -5 })).toBe(0);
    expect(wholeNodeJsonPath(wholeNodeRow({ type: 'nth', n: 0 }))).toBe('$.name[0]');
  });

  it('strips the resource prefix the node id carries, and leaves a value row alone', () => {
    expect(wholeNodeJsonPath(wholeNodeRow({ type: 'first' }))).toBe('$.name[0]');
    expect(wholeNodeJsonPath(valueRow)).toBeUndefined();
  });

  it('handles a nested node under a repeating parent', () => {
    const nested: MappingRow = {
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.contact.name',
      instance: { type: 'nth', n: 2 }, targetName: 'ContactNameJson', tableName: 'dbo.Patient',
    };
    expect(wholeNodeJsonPath(nested)).toBe('$.contact.name[1]');
  });
});
