import {
  buildMappingSummaryDocument, applyMappingSummaryDocument, pruneOrphanedMappingRows,
  ChildTableRelation, MappingSummaryDocument, qualify, bareName, reconcileRestoredTablesWithLiveSchema,
} from './field-mapping-summary.model';
import { MappingRow } from './field-mapping-model';
import { DestinationTable } from '../../../../services/destination-schema.service';
import { ResourceFieldDef } from '../destination-wizard.component';

describe('qualify', () => {
  it('prefixes a bare name with "dbo." for SQL Server only', () => {
    expect(qualify('Patient', 'sql')).toBe('dbo.Patient');
  });

  it('does NOT prefix "dbo." for MySQL, PostgreSQL, or Mongo — the exact bug this fixed: a MySQL/Postgres/Mongo destination default-guessing "dbo.Patient" instead of the bare name', () => {
    expect(qualify('Patient', 'mysql')).toBe('Patient');
    expect(qualify('Patient', 'postgres')).toBe('Patient');
    expect(qualify('Patient', 'mongo')).toBe('Patient');
  });

  it('leaves an already-qualified name (contains a dot) untouched regardless of destType', () => {
    expect(qualify('custom.Patient', 'sql')).toBe('custom.Patient');
  });
});

describe('bareName', () => {
  it('strips everything up to and including the last dot', () => {
    expect(bareName('dbo.Patient')).toBe('Patient');
  });

  it('returns the name unchanged when there is no dot to strip', () => {
    expect(bareName('Patient')).toBe('Patient');
  });
});

// ── reconcileRestoredTablesWithLiveSchema (stale-mapping-restoration fix) ───────────────────────────────
// Root cause traced: applyMappingSummaryDocument runs synchronously at reopen time, before any live schema
// probe has resolved, so it reconstructs every saved (always-bare) table name via qualify() alone — which
// only ever prefixes "dbo." for SQL Server. Once a real live probe DOES arrive
// (DestinationWizardComponent._refreshSqlTablesFromLiveSchema), this function re-resolves anything still
// unconfirmed against it, reusing findConfirmedTableMatch rather than a second matching implementation.
function liveTable(fullName: string, tableName: string, origin?: 'probed' | 'userCreated' | 'restoredUnverified') {
  return { fullName, tableName, origin };
}

function row(overrides: Partial<MappingRow> = {}): MappingRow {
  return {
    resource: 'Practitioner', sources: [{ fhirPath: 'Practitioner.name.text', label: 'Name' }],
    mode: 'value', instance: { type: 'first' }, targetName: 'Name', tableName: 'Practitioner',
    ...overrides,
  };
}

describe('reconcileRestoredTablesWithLiveSchema', () => {
  // scenario A
  it('MySQL: resolves a saved bare "Practitioner" against a live table whose fullName is "fhirbridge_output.Practitioner"', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ tableName: 'Practitioner' })],
        targetByResource: { Practitioner: 'Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('fhirbridge_output.Practitioner', 'Practitioner', 'probed')],
    );

    expect(result.targetByResource['Practitioner']).toBe('fhirbridge_output.Practitioner');
    expect(result.mappingRows[0].tableName).toBe('fhirbridge_output.Practitioner');
  });

  // scenario B
  it('PostgreSQL: resolves a saved bare "Practitioner" against a live table whose fullName is "public.Practitioner"', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ tableName: 'Practitioner' })],
        targetByResource: { Practitioner: 'Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('public.Practitioner', 'Practitioner', 'probed')],
    );

    expect(result.targetByResource['Practitioner']).toBe('public.Practitioner');
    expect(result.mappingRows[0].tableName).toBe('public.Practitioner');
  });

  // scenario C
  it('SQL Server: a saved "dbo.Practitioner" that already matches the live fullName is left completely unchanged', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ tableName: 'dbo.Practitioner' })],
        targetByResource: { Practitioner: 'dbo.Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('dbo.Practitioner', 'Practitioner', 'probed')],
    );

    expect(result.targetByResource['Practitioner']).toBe('dbo.Practitioner');
    expect(result.mappingRows[0].tableName).toBe('dbo.Practitioner');
  });

  // scenario D — the exact bug reported: "place" doesn't exist when only Id/Name are real
  it('resolves the STALE ROW\'S TABLE reference so the existing column-existence check can catch it — never renames/drops the column itself, never invents it', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ targetName: 'place', tableName: 'Practitioner' })],
        targetByResource: { Practitioner: 'Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      // The live table genuinely only has Id/Name — "place" is not among its columns.
      [liveTable('fhirbridge_output.Practitioner', 'Practitioner', 'probed')],
    );

    // Table reference is now correct (so validateMappingForSave's table-existence check passes)...
    expect(result.mappingRows[0].tableName).toBe('fhirbridge_output.Practitioner');
    // ...but the column name itself is untouched — this function never silently recreates or drops it;
    // validateMappingForSave's own, already-existing column-existence check is what catches "place" from here.
    expect(result.mappingRows[0].targetName).toBe('place');
  });

  // scenario E — a mapping that's already valid must be preserved exactly
  it('leaves a row already targeting the real, live-confirmed table/column untouched', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ targetName: 'Name', tableName: 'fhirbridge_output.Practitioner' })],
        targetByResource: { Practitioner: 'fhirbridge_output.Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('fhirbridge_output.Practitioner', 'Practitioner', 'probed')],
    );

    expect(result.mappingRows[0].tableName).toBe('fhirbridge_output.Practitioner');
    expect(result.mappingRows[0].targetName).toBe('Name');
  });

  // scenario F — a table created/staged THIS session (origin 'userCreated') counts as already-confirmed
  it('treats a "userCreated" live table as already confirmed — no rename attempted when it already matches', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ tableName: 'fhirbridge_output.Practitioner' })],
        targetByResource: { Practitioner: 'fhirbridge_output.Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('fhirbridge_output.Practitioner', 'Practitioner', 'userCreated')],
    );

    expect(result.targetByResource['Practitioner']).toBe('fhirbridge_output.Practitioner');
  });

  // scenario G — a 'restoredUnverified' entry must never be treated as a confirmed match (defensive; in
  // practice liveTables passed here is always freshly 'probed', but the function's own exclusion must hold
  // regardless of what's passed, matching findConfirmedTableMatch's own guarantee).
  it('does NOT resolve against a "restoredUnverified" table even if its name would otherwise match', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ tableName: 'Practitioner' })],
        targetByResource: { Practitioner: 'Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('fhirbridge_output.Practitioner', 'Practitioner', 'restoredUnverified')],
    );

    expect(result.targetByResource['Practitioner']).toBe('Practitioner');
    expect(result.mappingRows[0].tableName).toBe('Practitioner');
  });

  it('leaves a table reference untouched when no live table matches it at all — a genuinely deleted table', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [row({ tableName: 'Practitioner' })],
        targetByResource: { Practitioner: 'Practitioner' },
        extraTablesByGroup: {},
        childTableRelationsByTable: {},
      },
      [liveTable('fhirbridge_output.Other', 'Other', 'probed')],
    );

    expect(result.targetByResource['Practitioner']).toBe('Practitioner');
    expect(result.mappingRows[0].tableName).toBe('Practitioner');
  });

  it('also resolves extraTablesByGroup entries and childTableRelationsByTable keys/parentTable', () => {
    const result = reconcileRestoredTablesWithLiveSchema(
      {
        mappingRows: [],
        targetByResource: { Practitioner: 'Practitioner' },
        extraTablesByGroup: { Practitioner: ['PractitionerRole'] },
        childTableRelationsByTable: {
          PractitionerRole: { parentTable: 'Practitioner', parentColumn: 'Id', foreignKeyColumnName: 'PractitionerId' },
        },
      },
      [
        liveTable('fhirbridge_output.Practitioner', 'Practitioner', 'probed'),
        liveTable('fhirbridge_output.PractitionerRole', 'PractitionerRole', 'probed'),
      ],
    );

    expect(result.extraTablesByGroup['Practitioner']).toEqual(['fhirbridge_output.PractitionerRole']);
    const relation = result.childTableRelationsByTable['fhirbridge_output.PractitionerRole'];
    expect(relation).toBeDefined();
    expect(relation.parentTable).toBe('fhirbridge_output.Practitioner');
  });
});

const PATIENT_FIELDS: ResourceFieldDef[] = [
  { label: 'Id', path: 'Patient.id', sqlColumn: 'Id', csvColumn: 'Id', jsonPath: '$.id', valueType: 'String' },
  { label: 'Use', path: 'Patient.name.use', sqlColumn: 'Use', csvColumn: 'Use', jsonPath: '$.name[*].use', valueType: 'String', arrays: ['name'] },
  { label: 'Family', path: 'Patient.name.family', sqlColumn: 'Family', csvColumn: 'Family', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
];

function availableFields(r: string): ResourceFieldDef[] { return r === 'Patient' ? PATIENT_FIELDS : []; }

describe('buildMappingSummaryDocument', () => {
  it('wraps every mapped resource under {source, destination, mappings[]}', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value',
      instance: { type: 'first' }, targetName: 'Id', tableName: 'dbo.Patient',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields,
      sourceConnectionId: 'conn-123', destinationId: 'dest-456',
    });
    expect(doc.source).toBe('EPIC');
    expect(doc.destination).toBe('SQL');
    expect(doc.sourceConnectionId).toBe('conn-123');
    expect(doc.destinationId).toBe('dest-456');
    expect(doc.mappings.length).toBe(1);
    expect(doc.mappings[0].resourceType).toBe('Patient');
    expect(doc.mappings[0].rank).toBe(0); // Patient has no required dependency
    expect(doc.mappings[0].destination).toEqual({ type: 'sql', label: 'SQL Server' });
  });

  it('orders mappings[] by rank, not by first-appearance in mappingRows', () => {
    // Observation (rank 1, requires Patient) mapped first, Patient (rank 0) mapped second — the output
    // must still put Patient ahead of Observation.
    const rows: MappingRow[] = [
      { resource: 'Observation', sources: [{ fhirPath: 'Observation.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Observation' },
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Patient' },
    ];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields,
      sourceConnectionId: null, destinationId: null,
    });
    expect(doc.mappings.map(m => m.resourceType)).toEqual(['Patient', 'Observation']);
    expect(doc.mappings[0].rank).toBe(0);
    expect(doc.mappings[1].rank).toBe(1);
  });

  it('emits directField for a single source, with instance null when there is no array ancestor', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value',
      instance: { type: 'first' }, targetName: 'Id', tableName: 'dbo.Patient',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });
    const col = doc.mappings[0].tables[0].columns[0];
    expect(col).toEqual({ column: 'Id', mode: 'directField', sources: ['Patient.id'], instance: null });
  });

  it('emits joinedFields with delimiter for multi-source rows, and an arrayContext when one exists', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient',
      sources: [
        { fhirPath: 'Patient.name.use', label: 'Use', arrays: ['name'] },
        { fhirPath: 'Patient.name.family', label: 'Family', arrays: ['name'] },
      ],
      mode: 'value', delimiter: ',', instance: { type: 'all', aggregate: 'rows' },
      targetName: 'NameCombo', tableName: 'dbo.PatientNameCombo',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });
    const col = doc.mappings[0].tables[0].columns[0];
    expect(col.mode).toBe('joinedFields');
    expect(col.sources).toEqual(['Patient.name.use', 'Patient.name.family']);
    expect(col.delimiter).toBe(',');
    expect(col.instance).toEqual({ arrayContext: 'Patient.name', type: 'all', aggregate: 'rows' });
  });

  it('emits wholeNodeAsJson with sourceNode for childJson rows', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.name',
      targetName: 'NameJson', tableName: 'dbo.Patient',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });
    const col = doc.mappings[0].tables[0].columns[0];
    expect(col.mode).toBe('wholeNodeAsJson');
    expect(col.sourceNode).toBe('Patient.name');
    expect(col.sources).toBeUndefined();
  });

  it('marks a table userCreated as isNew, with the FK column flagged and its parent referenced', () => {
    const relation: ChildTableRelation = { parentTable: 'dbo.Patient', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' };
    const sqlTables: DestinationTable[] = [{
      schemaName: 'dbo', tableName: 'PatientName', fullName: 'dbo.PatientName', origin: 'userCreated',
      columns: [
        { name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null },
        { name: 'PatientId', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null },
        { name: 'Use', dataType: 'nvarchar(120)', mappingValueType: 'string', isNullable: true, maxLength: 120 },
      ],
    }];
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.use', label: 'Use', arrays: ['name'] }],
      mode: 'value', instance: { type: 'all', aggregate: 'rows' }, targetName: 'Use', tableName: 'dbo.PatientName',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: { 'dbo.PatientName': relation }, availableFields, sourceConnectionId: null, destinationId: null,
    });
    const table = doc.mappings[0].tables[0];
    expect(table.isNew).toBeTrue();
    expect(table.relation).toEqual({ childColumn: 'PatientId', parentTable: 'Patient', parentColumn: 'Id' });

    const created = doc.mappings[0].schemaChanges.tablesToCreate[0];
    expect(created.name).toBe('PatientName');
    expect(created.columns.find(c => c.name === 'Id')?.isPrimaryKey).toBeTrue();
    expect(created.columns.find(c => c.name === 'PatientId')).toEqual({
      name: 'PatientId', dataType: 'bigint', isPrimaryKey: false, isForeignKey: true, references: 'Patient.Id',
    });
  });

  it('lists a userCreated column on an already-existing table under columnsToAdd', () => {
    const sqlTables: DestinationTable[] = [{
      schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient', origin: 'probed',
      columns: [
        { name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null },
        { name: 'Active', dataType: 'bit', mappingValueType: 'bool', isNullable: true, maxLength: null, origin: 'userCreated' },
      ],
    }];
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.active', label: 'Active' }], mode: 'value',
      instance: { type: 'first' }, targetName: 'Active', tableName: 'dbo.Patient',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });
    expect(doc.mappings[0].schemaChanges.columnsToAdd).toEqual([{ table: 'Patient', name: 'Active', dataType: 'bit' }]);
    expect(doc.mappings[0].schemaChanges.tablesToCreate).toEqual([]);
  });

  it('queues a "restoredUnverified" table for creation rather than silently assuming it already exists (scenario G — the dbo.Patient investigation fix)', () => {
    // Exactly the reported bug's shape: dbo.Patient was reconstructed from a saved mapping on reopen
    // (origin 'restoredUnverified' — see applyMappingSummaryDocument) and this session's own live re-probe
    // either hasn't landed yet or failed — Save must NOT treat it as confirmed-real. It must be queued for
    // creation instead (schemaChanges.tablesToCreate), so the backend's idempotent CreateTableAsync either
    // creates it for real or safely no-ops if it turns out to already exist — never silently persisting a
    // MappingProfile that points at a table nothing has confirmed and nothing will create.
    const sqlTables: DestinationTable[] = [{
      schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient', origin: 'restoredUnverified',
      columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }],
    }];
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value',
      instance: { type: 'first' }, targetName: 'Id', tableName: 'dbo.Patient',
    }];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });

    expect(doc.mappings[0].tables[0].isNew).toBeTrue();
    expect(doc.mappings[0].schemaChanges.tablesToCreate.map(t => t.name)).toEqual(['Patient']);
    expect(doc.mappings[0].schemaChanges.columnsToAdd).toEqual([]);
  });

  it('does NOT re-queue for creation a table confirmed real by a live probe (origin undefined/"probed") or already userCreated this session — no regression from the restoredUnverified fix', () => {
    const sqlTables: DestinationTable[] = [
      { schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient', origin: undefined, columns: [] },
      { schemaName: 'dbo', tableName: 'Encounter', fullName: 'dbo.Encounter', origin: 'probed', columns: [] },
      { schemaName: 'dbo', tableName: 'Observation', fullName: 'dbo.Observation', origin: 'userCreated', columns: [] },
    ];
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Patient' },
      { resource: 'Encounter', sources: [{ fhirPath: 'Encounter.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Encounter' },
      { resource: 'Observation', sources: [{ fhirPath: 'Observation.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Observation' },
    ];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });

    expect(doc.mappings.find(m => m.resourceType === 'Patient')!.tables[0].isNew).toBeFalse();
    expect(doc.mappings.find(m => m.resourceType === 'Encounter')!.tables[0].isNew).toBeFalse();
    expect(doc.mappings.find(m => m.resourceType === 'Observation')!.tables[0].isNew).toBeTrue(); // userCreated IS new — confirmed via pendingTableNames elsewhere, not this flag
  });

  it('orders processingOrder parent-before-child by dependency depth', () => {
    const relation: ChildTableRelation = { parentTable: 'dbo.Patient', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' };
    const sqlTables: DestinationTable[] = [
      { schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient', origin: 'probed', columns: [] },
      { schemaName: 'dbo', tableName: 'PatientName', fullName: 'dbo.PatientName', origin: 'userCreated', columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }] },
    ];
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Patient' },
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.name.use', label: 'Use', arrays: ['name'] }], mode: 'value', targetName: 'Use', tableName: 'dbo.PatientName' },
    ];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: { 'dbo.PatientName': relation }, availableFields, sourceConnectionId: null, destinationId: null,
    });
    const order = doc.mappings[0].processingOrder;
    expect(order.find(o => o.table === 'Patient')!.level).toBe(1);
    expect(order.find(o => o.table === 'PatientName')!.level).toBe(2);
    expect(order.find(o => o.table === 'PatientName')!.dependsOn).toBe('Patient');
    expect(order[0].table).toBe('Patient'); // parent sorted before child
  });

  /**
   * Regression test for a real production bug: Encounter.PatientId has a genuine SQL foreign key to
   * Patient.Id (for referential integrity), and live-schema probing picked that up as a
   * childTableRelationsByTable entry — but Encounter is its own independent, top-level resource (its own
   * entry in doc.mappings), not a child sub-table of Patient's mapping. Treating it as one made Encounter's
   * own root table unrecoverable (every "find the root table" lookup in this file is "the one table with
   * no relation") and broke the canvas wire for the reference field mapped onto that same column.
   */
  it('drops a table-level relation whose parent belongs to a DIFFERENT resource entirely (a live-schema FK misread as a child-table relation)', () => {
    const bogusRelation: ChildTableRelation = { parentTable: 'dbo.Patient', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' };
    const sqlTables: DestinationTable[] = [
      { schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient', origin: 'probed', columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }] },
      { schemaName: 'dbo', tableName: 'Encounter', fullName: 'dbo.Encounter', origin: 'probed', columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }] },
    ];
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', targetName: 'PatientId', tableName: 'dbo.Patient' },
      {
        resource: 'Encounter', sources: [{ fhirPath: 'Encounter.subject.reference', label: 'Reference' }], mode: 'value',
        targetName: 'PatientId', tableName: 'dbo.Encounter', referencesResource: 'Patient',
      },
    ];

    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables,
      childTableRelationsByTable: { 'dbo.Encounter': bogusRelation }, // what live-schema probing mis-derived
      availableFields, sourceConnectionId: null, destinationId: null,
      targetByResource: { Patient: 'dbo.Patient', Encounter: 'dbo.Encounter' },
    });

    const encounterEntry = doc.mappings.find(m => m.resourceType === 'Encounter')!;
    expect(encounterEntry.tables[0].relation).toBeNull();
    expect(encounterEntry.processingOrder[0].dependsOn).toBeNull();

    // The column mapping itself is untouched — the reference still resolves via referenceLookup, just never
    // as a table-level relation.
    const patientIdColumn = encounterEntry.tables[0].columns.find(c => c.column === 'PatientId')!;
    expect(patientIdColumn.referenceLookup).toEqual({ table: 'Patient', keyColumn: 'PatientId' });
  });

  it('keeps a genuine same-resource child-table relation even when its root table has no directly-mapped row', () => {
    // Regression guard for the naive fix this test would have broken: inferring "siblings" from which
    // tables actually have a mapped row, instead of from targetByResource (each resource's independently
    // declared root) — PatientName's parent "Patient" must still count as this SAME resource even though no
    // row here targets "dbo.Patient" directly.
    const relation: ChildTableRelation = { parentTable: 'dbo.Patient', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' };
    const sqlTables: DestinationTable[] = [{
      schemaName: 'dbo', tableName: 'PatientName', fullName: 'dbo.PatientName', origin: 'userCreated',
      columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }],
    }];
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.name.use', label: 'Use', arrays: ['name'] }],
      mode: 'value', targetName: 'Use', tableName: 'dbo.PatientName',
    }];

    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: { 'dbo.PatientName': relation }, availableFields,
      sourceConnectionId: null, destinationId: null,
      targetByResource: { Patient: 'dbo.Patient' }, // Patient's own root, even though no row targets it here
    });

    expect(doc.mappings[0].tables[0].relation).toEqual({ childColumn: 'PatientId', parentTable: 'Patient', parentColumn: 'Id' });
  });
});

describe('applyMappingSummaryDocument (round-trip)', () => {
  it('reconstructs mappingRows, tables, and relations from a document built moments ago', () => {
    const relation: ChildTableRelation = { parentTable: 'dbo.Patient', parentColumn: 'Id', foreignKeyColumnName: 'PatientId' };
    const sqlTables: DestinationTable[] = [
      { schemaName: 'dbo', tableName: 'Patient', fullName: 'dbo.Patient', origin: 'probed', columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }] },
      {
        schemaName: 'dbo', tableName: 'PatientName', fullName: 'dbo.PatientName', origin: 'userCreated',
        columns: [
          { name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null },
          { name: 'PatientId', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null },
          { name: 'Use', dataType: 'nvarchar(120)', mappingValueType: 'string', isNullable: true, maxLength: null },
        ],
      },
    ];
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', instance: { type: 'first' }, targetName: 'Id', tableName: 'dbo.Patient' },
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.name.use', label: 'Use', arrays: ['name'] }], mode: 'value', instance: { type: 'all', aggregate: 'rows' }, targetName: 'Use', tableName: 'dbo.PatientName' },
    ];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables, childTableRelationsByTable: { 'dbo.PatientName': relation }, availableFields, sourceConnectionId: null, destinationId: null,
    });

    const applied = applyMappingSummaryDocument(doc, 'sql');

    expect(applied.selectedResources).toEqual(['Patient']);
    expect(applied.childTableRelationsByTable['dbo.PatientName']).toEqual(relation);
    expect(applied.targetByResource['Patient']).toBe('dbo.Patient');
    expect(applied.extraTablesByGroup['Patient']).toEqual(['dbo.PatientName']);
    expect(applied.sqlTables.find(t => t.fullName === 'dbo.PatientName')?.origin).toBe('userCreated');

    const idRow = applied.mappingRows.find(r => r.targetName === 'Id');
    expect(idRow?.sources[0].fhirPath).toBe('Patient.id');
    const useRow = applied.mappingRows.find(r => r.targetName === 'Use');
    expect(useRow?.sources[0].fhirPath).toBe('Patient.name.use');
    expect(useRow?.instance).toEqual({ type: 'all', n: undefined, field: undefined, op: undefined, value: undefined, aggregate: 'rows' });

    // Building again from the applied state reproduces the same document shape.
    const rebuilt = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: applied.mappingRows, sqlTables: applied.sqlTables,
      childTableRelationsByTable: applied.childTableRelationsByTable, availableFields, sourceConnectionId: null, destinationId: null,
    });
    expect(rebuilt.mappings[0].tables.map(t => t.name).sort()).toEqual(doc.mappings[0].tables.map(t => t.name).sort());
    expect(rebuilt.mappings[0].schemaChanges.tablesToCreate.map(t => t.name)).toEqual(doc.mappings[0].schemaChanges.tablesToCreate.map(t => t.name));
  });

  it('tags a table restored from a saved document "restoredUnverified" — never undefined/"probed" (scenario D — the dbo.Patient investigation fix)', () => {
    // isNew: false in the saved doc means "this table was NOT created during that earlier save" — before
    // this fix that silently became origin: undefined, bit-for-bit indistinguishable from a table this
    // session's own live probe just confirmed. Reopening a mapping must never make that assumption on its
    // own; only DestinationWizardComponent._refreshSqlTablesFromLiveSchema's real live re-probe may upgrade
    // (or evict) it.
    const doc: MappingSummaryDocument = {
      source: 'EPIC', destination: 'SQL', sourceConnectionId: null, destinationId: null,
      mappings: [{
        resourceType: 'Patient', rank: 0, generatedAt: '2026-01-01T00:00:00.000Z',
        schemaChanges: { tablesToCreate: [], columnsToAdd: [], summary: '' },
        processingOrder: [{ step: 1, table: 'Patient', level: 1, dependsOn: null, note: null }],
        destination: { type: 'sql', label: 'SQL Server' },
        tables: [{
          name: 'Patient', isNew: false, relation: null,
          columns: [{ column: 'Id', mode: 'directField', sources: ['Patient.id'], instance: null }],
        }],
      }],
    };

    const applied = applyMappingSummaryDocument(doc, 'sql');

    const patientTable = applied.sqlTables.find(t => t.fullName === 'dbo.Patient');
    expect(patientTable?.origin).toBe('restoredUnverified');
    // The mapping row itself (what the user actually mapped) still comes back untouched — only the
    // table's confirmed-existence status is affected, never the mapping content.
    expect(applied.mappingRows.find(r => r.targetName === 'Id')?.tableName).toBe('dbo.Patient');
  });

  it('a table the saved document DID mark isNew (created and confirmed during that earlier save) is tagged "userCreated", not "restoredUnverified"', () => {
    const doc: MappingSummaryDocument = {
      source: 'EPIC', destination: 'SQL', sourceConnectionId: null, destinationId: null,
      mappings: [{
        resourceType: 'Patient', rank: 0, generatedAt: '2026-01-01T00:00:00.000Z',
        schemaChanges: { tablesToCreate: [], columnsToAdd: [], summary: '' },
        processingOrder: [{ step: 1, table: 'Patient', level: 1, dependsOn: null, note: null }],
        destination: { type: 'sql', label: 'SQL Server' },
        tables: [{
          name: 'Patient', isNew: true, relation: null,
          columns: [{ column: 'Id', mode: 'directField', sources: ['Patient.id'], instance: null }],
        }],
      }],
    };

    const applied = applyMappingSummaryDocument(doc, 'sql');

    expect(applied.sqlTables.find(t => t.fullName === 'dbo.Patient')?.origin).toBe('userCreated');
  });

  it('self-heals a document saved BEFORE the cross-resource-relation guard existed — loading it must not restore the bad relation even transiently', () => {
    // Hand-built (not via buildMappingSummaryDocument) to simulate exactly what's actually on disk from
    // before this fix: Encounter's only table wrongly carries a relation pointing at Patient's own root.
    const badDoc: MappingSummaryDocument = {
      source: 'EPIC', destination: 'SQL', sourceConnectionId: null, destinationId: null,
      mappings: [
        {
          resourceType: 'Patient', rank: 0, generatedAt: '2026-01-01T00:00:00.000Z',
          schemaChanges: { tablesToCreate: [], columnsToAdd: [], summary: '' },
          processingOrder: [{ step: 1, table: 'Patient', level: 1, dependsOn: null, note: null }],
          destination: { type: 'sql', label: 'SQL Server' },
          tables: [{
            name: 'Patient', isNew: false, relation: null,
            columns: [{ column: 'PatientId', mode: 'directField', sources: ['Patient.id'], instance: null }],
          }],
        },
        {
          resourceType: 'Encounter', rank: 1, generatedAt: '2026-01-01T00:00:00.000Z',
          schemaChanges: { tablesToCreate: [], columnsToAdd: [], summary: '' },
          processingOrder: [{ step: 1, table: 'Encounter', level: 1, dependsOn: 'Patient', note: null }],
          destination: { type: 'sql', label: 'SQL Server' },
          tables: [{
            name: 'Encounter',
            isNew: false,
            relation: { childColumn: 'PatientId', parentTable: 'Patient', parentColumn: 'Id' }, // the bug
            columns: [{
              column: 'PatientId', mode: 'directField', sources: ['Encounter.subject.reference'], instance: null,
              referenceLookup: { table: 'Patient', keyColumn: 'PatientId' },
            }],
          }],
        },
      ],
    };

    const applied = applyMappingSummaryDocument(badDoc, 'sql');

    expect(applied.childTableRelationsByTable['dbo.Encounter']).toBeUndefined();
    expect(applied.targetByResource['Encounter']).toBe('dbo.Encounter');
    const patientIdRow = applied.mappingRows.find(r => r.resource === 'Encounter' && r.targetName === 'PatientId');
    expect(patientIdRow?.referencesResource).toBe('Patient');
  });
});

describe('reference lookup (referencesResource)', () => {
  it('resolves a reference field against the OTHER resource\'s own table + id column, regardless of mapping order', () => {
    // Observation's PatientId field is mapped BEFORE Patient's own Id field — the lookup must still resolve
    // correctly, since it can't be derived until every resource's own key column is known.
    const rows: MappingRow[] = [
      {
        resource: 'Observation', sources: [{ fhirPath: 'Observation.subject.reference', label: 'reference' }],
        mode: 'value', targetName: 'PatientId', tableName: 'dbo.Observation', referencesResource: 'Patient',
      },
      { resource: 'Observation', sources: [{ fhirPath: 'Observation.id', label: 'Id' }], mode: 'value', targetName: 'Id', tableName: 'dbo.Observation' },
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', targetName: 'PatientId', tableName: 'dbo.Patient' },
    ];

    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields,
      sourceConnectionId: null, destinationId: null,
    });

    const observationTable = doc.mappings.find(m => m.resourceType === 'Observation')!.tables[0];
    const patientIdColumn = observationTable.columns.find(c => c.column === 'PatientId')!;
    expect(patientIdColumn.referenceLookup).toEqual({ table: 'Patient', keyColumn: 'PatientId' });

    // A column with no referencesResource must not carry a referenceLookup key at all.
    const idColumn = observationTable.columns.find(c => c.column === 'Id')!;
    expect(idColumn.referenceLookup).toBeUndefined();
  });

  it('round-trips referencesResource through apply → rebuild', () => {
    const rows: MappingRow[] = [
      {
        resource: 'Observation', sources: [{ fhirPath: 'Observation.subject.reference', label: 'reference' }],
        mode: 'value', targetName: 'PatientId', tableName: 'dbo.Observation', referencesResource: 'Patient',
      },
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', targetName: 'PatientId', tableName: 'dbo.Patient' },
    ];

    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'sql', destLabel: 'SQL Server',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {}, availableFields,
      sourceConnectionId: null, destinationId: null,
    });

    const applied = applyMappingSummaryDocument(doc, 'sql');
    const restoredRow = applied.mappingRows.find(r => r.resource === 'Observation' && r.targetName === 'PatientId');
    expect(restoredRow?.referencesResource).toBe('Patient');
  });
});

describe('pruneOrphanedMappingRows', () => {
  const encounterTable: DestinationTable = {
    schemaName: 'dbo', tableName: 'Encounter', fullName: 'dbo.Encounter', origin: 'probed',
    columns: [
      { name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null },
      { name: 'Identifier', dataType: 'nvarchar(200)', mappingValueType: 'string', isNullable: true, maxLength: null },
    ],
  };

  it('drops a row whose target column was renamed/dropped directly in the database, left behind in the canvas', () => {
    const rows: MappingRow[] = [
      { resource: 'Encounter', sources: [{ fhirPath: 'Encounter.id', label: 'Id' }], mode: 'value', targetName: 'Identifier', tableName: 'dbo.Encounter' },
      // Stale: "EncounterId" isn't a real column on dbo.Encounter (renamed to "Identifier" outside the wizard).
      { resource: 'Encounter', sources: [{ fhirPath: 'Encounter.id', label: 'Id' }], mode: 'value', targetName: 'EncounterId', tableName: 'dbo.Encounter' },
    ];

    const pruned = pruneOrphanedMappingRows(rows, [encounterTable]);

    expect(pruned).toHaveSize(1);
    expect(pruned[0].targetName).toBe('Identifier');
  });

  it('keeps every row for a table this session has never probed/created yet (nothing to validate against)', () => {
    const rows: MappingRow[] = [
      { resource: 'Procedure', sources: [{ fhirPath: 'Procedure.id', label: 'Id' }], mode: 'value', targetName: 'ProcedureId', tableName: 'dbo.PatientProcedure' },
    ];

    const pruned = pruneOrphanedMappingRows(rows, [encounterTable]); // dbo.PatientProcedure not in sqlTables at all

    expect(pruned).toHaveSize(1);
  });

  it('keeps a column that IS on the table, including one added this session (userCreated, not yet live)', () => {
    const tableWithNewColumn: DestinationTable = {
      ...encounterTable,
      columns: [
        ...encounterTable.columns,
        { name: 'NewNotes', dataType: 'nvarchar(max)', mappingValueType: 'string', isNullable: true, maxLength: null, origin: 'userCreated' },
      ],
    };
    const rows: MappingRow[] = [
      { resource: 'Encounter', sources: [{ fhirPath: 'Encounter.text.div', label: 'Notes' }], mode: 'value', targetName: 'NewNotes', tableName: 'dbo.Encounter' },
    ];

    const pruned = pruneOrphanedMappingRows(rows, [tableWithNewColumn]);

    expect(pruned).toHaveSize(1);
  });
});
