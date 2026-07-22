import { buildMappingSummaryDocument, applyMappingSummaryDocument, ChildTableRelation } from './field-mapping-summary.model';
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
});
