import {
  buildMappingSummaryDocument, applyMappingSummaryDocument, pruneOrphanedMappingRows, tableNameMatches,
  ChildTableRelation, MappingSummaryDocument,
} from './field-mapping-summary.model';
import { MappingRow } from './field-mapping-model';
import { DestinationTable } from '../../../../services/destination-schema.service';
import { ResourceFieldDef } from '../destination-wizard.component';

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

// Save (build) -> reopen (apply) per destType — the exact round trip a user's browser session does when
// they close and reopen a saved mapping. The summary document itself always stores bare table names
// (bareName() at build time — see this file's own header comment); qualifyTableName re-qualifies them for
// the destType the document was applied against. This is the fix for: reopening a PostgreSQL mapping
// used to restore "Appointment" instead of "public.Appointment" (the old qualify() only ever added "dbo.").
describe('applyMappingSummaryDocument — save/reopen per destType', () => {
  function buildAndReapply(destType: 'sql' | 'postgres' | 'mysql', tableFullName: string) {
    const sqlTables: DestinationTable[] = [{
      schemaName: tableFullName.includes('.') ? tableFullName.split('.')[0] : '',
      tableName: tableFullName.includes('.') ? tableFullName.split('.')[1] : tableFullName,
      fullName: tableFullName, origin: 'probed',
      columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'int', isNullable: false, maxLength: null }],
    }];
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', instance: { type: 'first' }, targetName: 'Id', tableName: tableFullName },
    ];
    const doc = buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType, destLabel: destType,
      mappingRows: rows, sqlTables, childTableRelationsByTable: {}, availableFields, sourceConnectionId: null, destinationId: null,
    });
    return applyMappingSummaryDocument(doc, destType);
  }

  it('PostgreSQL: save then reopen restores "public.Patient", not bare "Patient"', () => {
    const applied = buildAndReapply('postgres', 'public.Patient');
    expect(applied.targetByResource['Patient']).toBe('public.Patient');
  });

  it('MySQL: save then reopen restores bare "Patient" (no schema layer)', () => {
    const applied = buildAndReapply('mysql', 'Patient');
    expect(applied.targetByResource['Patient']).toBe('Patient');
  });

  it('SQL Server: save then reopen restores "dbo.Patient" unchanged (pre-existing behavior, not regressed)', () => {
    const applied = buildAndReapply('sql', 'dbo.Patient');
    expect(applied.targetByResource['Patient']).toBe('dbo.Patient');
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

describe('tableNameMatches', () => {
  const probed: DestinationTable = {
    schemaName: 'dbo', tableName: 'Patient_NewMapped', fullName: 'dbo.Patient_NewMapped', origin: 'probed',
    columns: [],
  };

  it('matches the fully qualified name', () => {
    expect(tableNameMatches(probed, 'dbo.Patient_NewMapped')).toBe(true);
  });

  it('matches the bare name — the spelling a queued "add column" op and a restored mapping row both carry', () => {
    // Regression: this fallback used to be gated on MySQL only, so for SQL Server the lookup in
    // onColumnAdded silently matched nothing. The real ALTER TABLE succeeded but the new column never
    // landed in sqlTables(), and pruneOrphanedMappingRows then deleted the row mapped onto it, with no
    // error, on every save — the user's column came back but their mapping for it did not.
    expect(tableNameMatches(probed, 'Patient_NewMapped')).toBe(true);
  });

  it('matches case-insensitively (SQL Server/MySQL identifiers are not case-sensitive by default)', () => {
    expect(tableNameMatches(probed, 'DBO.PATIENT_NEWMAPPED')).toBe(true);
  });

  it('does not match a genuinely different table', () => {
    expect(tableNameMatches(probed, 'dbo.Encounter')).toBe(false);
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

  it('keeps a row mapped onto a column that really exists, when the row carries the BARE table name', () => {
    // The row's tableName is the bare spelling while the probe reports the qualified one — the same
    // mismatch tableNameMatches exists to absorb. Before that, this lookup missed, the row fell into the
    // "table not found, insufficient evidence" branch and survived by luck rather than by checking; a row
    // naming a genuinely dropped column on a bare-named table survived too. Now it resolves the table for
    // real, so a valid column is kept deliberately.
    const rows: MappingRow[] = [
      { resource: 'Encounter', sources: [{ fhirPath: 'Encounter.id', label: 'Id' }], mode: 'value', targetName: 'Identifier', tableName: 'Encounter' },
    ];

    expect(pruneOrphanedMappingRows(rows, [encounterTable])).toHaveSize(1);
  });

  it('drops a stale column on a bare-named table (the lookup now resolves instead of silently missing)', () => {
    const rows: MappingRow[] = [
      { resource: 'Encounter', sources: [{ fhirPath: 'Encounter.id', label: 'Id' }], mode: 'value', targetName: 'NotAColumn', tableName: 'Encounter' },
    ];

    expect(pruneOrphanedMappingRows(rows, [encounterTable])).toHaveSize(0);
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

  it('never prunes against a table restored only from a saved Mapping JSON (no live probe) — the shared-CSV-table regression', () => {
    // A single CSV output table two resources both write into. Restored via applyMappingSummaryDocument
    // from a PREVIOUS save that only knew about Patient's own column — CSV has no live schema to probe at
    // all (_refreshSqlTablesFromLiveSchema only ever runs for SQL-family destinations), so this table's
    // origin is never 'probed', only 'userCreated' or undefined, regardless of how current the session is.
    const sharedCsvTable: DestinationTable = {
      schemaName: 'dbo', tableName: 'csv', fullName: 'csv', origin: 'userCreated',
      columns: [
        { name: 'Patient@now', dataType: 'string', mappingValueType: 'string', isNullable: true, maxLength: null },
      ],
    };
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.@now', label: 'now' }], mode: 'value', targetName: 'Patient@now', tableName: 'csv' },
      // Just mapped THIS session, onto the same shared table — not yet reflected in sharedCsvTable.columns
      // above (that snapshot predates this resource's own mapping) but a completely valid, current row.
      { resource: 'Organization', sources: [{ fhirPath: 'Organization.@now', label: 'now' }], mode: 'value', targetName: 'Organization@now', tableName: 'csv' },
    ];

    const pruned = pruneOrphanedMappingRows(rows, [sharedCsvTable]);

    expect(pruned).toHaveSize(2);
    expect(pruned.map(r => r.resource)).toEqual(['Patient', 'Organization']);
  });

  it('still drops a genuinely orphaned column on a LIVE-probed table even when another resource shares it', () => {
    // The authoritative case this fix must not weaken: once a shared table's columns actually come from a
    // live probe, a target column that really isn't there anymore is still pruned, same as before.
    const sharedProbedTable: DestinationTable = {
      schemaName: 'dbo', tableName: 'Shared', fullName: 'dbo.Shared', origin: 'probed',
      columns: [
        { name: 'PatientField', dataType: 'nvarchar(50)', mappingValueType: 'string', isNullable: true, maxLength: null },
      ],
    };
    const rows: MappingRow[] = [
      { resource: 'Patient', sources: [{ fhirPath: 'Patient.a', label: 'a' }], mode: 'value', targetName: 'PatientField', tableName: 'dbo.Shared' },
      { resource: 'Organization', sources: [{ fhirPath: 'Organization.a', label: 'a' }], mode: 'value', targetName: 'GoneField', tableName: 'dbo.Shared' },
    ];

    const pruned = pruneOrphanedMappingRows(rows, [sharedProbedTable]);

    expect(pruned).toHaveSize(1);
    expect(pruned[0].resource).toBe('Patient');
  });
});
