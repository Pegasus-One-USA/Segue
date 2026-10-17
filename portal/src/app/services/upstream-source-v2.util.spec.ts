import { CanvasNode } from '../models/node-v2.model';
import { FHIR_RESOURCES } from '../data/scope-constants-v2.data';
import {
  declaredSourceResourceTypes,
  declaredTypesOfSources,
  destinationWrittenResourceTypes,
  findDestinationTypesOutsideSource,
  firstUpstreamSourceId,
  SOURCE_TYPES_DECLARED_KEY,
  upstreamSourceIds,
  upstreamSourceNodes,
} from './upstream-source-v2.util';

const source = (id: string, fields: Record<string, string>): CanvasNode =>
  ({ id, x: 0, y: 0, connected: true, fields }) as CanvasNode;
const transform = (id: string, transformId: string, fields: Record<string, string> = {}): CanvasNode =>
  ({ id, x: 0, y: 0, kind: 'transform', transformId, fields }) as CanvasNode;
const edge = (from: string, to: string) => ({ id: `${from}-${to}`, from, to });
const declared = { [SOURCE_TYPES_DECLARED_KEY]: 'true' };
/** The 12 types every Generic FHIR node saved before Step B carries in 'Resources' without having chosen them. */
const GENERIC_FHIR_SILENT_DEFAULT = FHIR_RESOURCES.join(',');

describe('upstream-source-v2.util', () => {
  describe('declaredSourceResourceTypes', () => {
    it('reads an EHR / Generic FHIR node\'s Resources once the node carries the declared-types marker', () => {
      expect(declaredSourceResourceTypes({ ...declared, Resources: 'Patient, Condition, patient' })).toEqual([
        'Patient',
        'Condition',
      ]);
    });

    it('reads the marker as the server does: "true" in any letter case, or a JSON boolean true', () => {
      for (const marker of ['TRUE', 'True', 'true']) {
        expect(declaredSourceResourceTypes({ [SOURCE_TYPES_DECLARED_KEY]: marker, Resources: 'Patient' })).toEqual(['Patient']);
      }
      const booleanMarker = { [SOURCE_TYPES_DECLARED_KEY]: true, Resources: 'Patient' } as unknown as Record<string, string>;
      expect(declaredSourceResourceTypes(booleanMarker)).toEqual(['Patient']);
      for (const marker of ['false', 'yes', '']) {
        expect(declaredSourceResourceTypes({ [SOURCE_TYPES_DECLARED_KEY]: marker, Resources: 'Patient' })).toBeNull();
      }
    });

    it('never reads Retrieval resource type, and treats an unmarked list as a legacy silent default', () => {
      // The server's ReadSourceDeclared does not read 'Retrieval resource type' either: the form pre-filled it.
      expect(declaredSourceResourceTypes({ ...declared, 'Retrieval resource type': 'Observation' })).toBeNull();
      expect(declaredSourceResourceTypes({ Resources: GENERIC_FHIR_SILENT_DEFAULT })).toBeNull();
      expect(declaredSourceResourceTypes({ Resources: 'Patient', 'Retrieval resource type': 'Patient' })).toBeNull();
    });

    it('reads a CSV / SQL Table node\'s tab_streams, or the older tab_templates', () => {
      expect(declaredSourceResourceTypes({
        tab_kind: 'sql',
        tab_streams: JSON.stringify([{ resourceType: 'Procedure', query: 'x' }, { resourceType: 'Condition' }]),
      })).toEqual(['Procedure', 'Condition']);
      expect(declaredSourceResourceTypes({
        tab_kind: 'csv',
        tab_templates: JSON.stringify([{ resourceType: 'AllergyIntolerance', template: {} }]),
      })).toEqual(['AllergyIntolerance']);
    });

    it('reads a legacy tab_templates entry\'s type off its template, as the server does', () => {
      expect(declaredSourceResourceTypes({
        tab_kind: 'csv',
        tab_templates: JSON.stringify([{ template: { resourceType: 'Immunization' } }]),
      })).toEqual(['Immunization']);
    });

    it('falls back to tab_templates when tab_streams parses to no types, then to Resources', () => {
      expect(declaredSourceResourceTypes({
        tab_kind: 'sql',
        tab_streams: '[]',
        tab_templates: JSON.stringify([{ resourceType: 'Condition', template: {} }]),
      })).toEqual(['Condition']);
      expect(declaredSourceResourceTypes({ tab_kind: 'sql', tab_streams: '[]', Resources: 'Procedure' })).toEqual([
        'Procedure',
      ]);
    });

    it('is null for a legacy node that declares nothing', () => {
      expect(declaredSourceResourceTypes({ Connector: 'Athenahealth', Resources: '' })).toBeNull();
      expect(declaredSourceResourceTypes({ tab_kind: 'sql', tab_streams: 'not json' })).toBeNull();
    });
  });

  it('destinationWrittenResourceTypes unions Data groups and mapped resources', () => {
    expect(destinationWrittenResourceTypes({
      dest_resources: 'Patient',
      dest_mappings: JSON.stringify([{ resource: 'Condition' }, { resource: 'Patient' }]),
    })).toEqual(['Patient', 'Condition']);
  });

  it('destinationWrittenResourceTypes ignores mapping rows whose resource is not a type token', () => {
    expect(destinationWrittenResourceTypes({
      dest_mappings: JSON.stringify([
        { resource: '' },
        { resource: 'Patient name' },
        { resource: 'condition' },
        { resource: 'Encounter' },
      ]),
    })).toEqual(['Encounter']);
  });

  describe('upstream walk', () => {
    // Two sources, each feeding its own destination through its own mapping node.
    const nodes = [
      source('s1', { __name: 'Epic', ...declared, Resources: 'Patient, Condition' }),
      source('s2', { __name: 'Rows', tab_kind: 'sql', tab_streams: JSON.stringify([{ resourceType: 'Procedure' }]) }),
      transform('m1', 'field-mapping'),
      transform('m2', 'field-mapping'),
      transform('d1', 'dest-sqlserver', { dest_name: 'Warehouse' }),
      transform('d2', 'dest-ehr-writeback', { dest_name: 'Write-back' }),
    ];
    const edges = [edge('s1', 'm1'), edge('m1', 'd1'), edge('s2', 'm2'), edge('m2', 'd2')];

    it('finds only the source each destination is wired to', () => {
      expect(upstreamSourceNodes('d1', nodes, edges).map((n) => n.id)).toEqual(['s1']);
      expect(upstreamSourceNodes('d2', nodes, edges).map((n) => n.id)).toEqual(['s2']);
      expect(declaredTypesOfSources(upstreamSourceNodes('d1', nodes, edges))).toEqual(['Patient', 'Condition']);
      expect(declaredTypesOfSources(upstreamSourceNodes('d2', nodes, edges))).toEqual(['Procedure']);
    });

    it('unions every source feeding a merge, and is null when one of them is legacy', () => {
      const merged = [...edges, edge('s2', 'm1')];
      expect(upstreamSourceIds('d1', merged, (id) => id.startsWith('s')).sort()).toEqual(['s1', 's2']);
      expect(declaredTypesOfSources(upstreamSourceNodes('d1', nodes, merged))?.sort()).toEqual(['Condition', 'Patient', 'Procedure']);

      const legacy = nodes.map((n) => (n.id === 's2' ? source('s2', { Connector: 'Athenahealth' }) : n));
      expect(declaredTypesOfSources(upstreamSourceNodes('d1', legacy, merged))).toBeNull();
    });

    it('firstUpstreamSourceId follows the first inbound edge, as the assembler always did', () => {
      expect(firstUpstreamSourceId('d2', edges, (id) => id.startsWith('s'))).toBe('s2');
      expect(firstUpstreamSourceId('m9', edges, (id) => id.startsWith('s'))).toBeNull();
    });
  });

  describe('findDestinationTypesOutsideSource', () => {
    const base = (destFields: Record<string, string>, sourceFields: Record<string, string>) => ({
      nodes: [source('s1', { __name: 'Epic Backend', ...sourceFields }), transform('d1', 'dest-sqlserver', destFields)],
      edges: [edge('s1', 'd1')],
    });

    it('names the destination, the types and the source', () => {
      const { nodes, edges } = base(
        { dest_name: 'Warehouse', dest_resources: 'Patient,Procedure' },
        { ...declared, Resources: 'Patient, Condition' },
      );
      // The builder names nodes by their saved displayName, as the server does; here, a stand-in.
      const nameOf = (n: CanvasNode) => (n.id === 'd1' ? 'SQL Server' : 'Epic Backend');
      const mismatch = findDestinationTypesOutsideSource(nodes, edges, nameOf);
      expect(mismatch?.types).toEqual(['Procedure']);
      expect(mismatch?.message).toBe(
        "Destination 'SQL Server' writes Procedure, which its source 'Epic Backend' does not read. " +
          "Add it to the source's resource types or remove it from the destination.",
      );
    });

    it('names a node by its saved name, else its id, by default (the server\'s fallback)', () => {
      const { nodes, edges } = base(
        { dest_name: 'Warehouse', dest_resources: 'Procedure' },
        { ...declared, Resources: 'Patient' },
      );
      expect(findDestinationTypesOutsideSource(nodes, edges)?.message).toContain(
        "Destination 'd1' writes Procedure, which its source 'Epic Backend' does not read.",
      );
    });

    it('also checks the resources a destination maps', () => {
      const { nodes, edges } = base(
        { dest_name: 'Warehouse', dest_resources: 'Patient', dest_mappings: JSON.stringify([{ resource: 'Encounter' }]) },
        { ...declared, Resources: 'Patient' },
      );
      expect(findDestinationTypesOutsideSource(nodes, edges)?.types).toEqual(['Encounter']);
    });

    it('compares types case-insensitively, as the server does', () => {
      const { nodes, edges } = base({ dest_resources: 'patient' }, { ...declared, Resources: 'Patient' });
      expect(findDestinationTypesOutsideSource(nodes, edges)).toBeNull();
    });

    it('lets a pre-Step-B Generic FHIR node (silent 12-type list, no marker) feed an Organization destination', () => {
      const { nodes, edges } = base(
        { dest_name: 'Aidbox', dest_resources: 'Patient, Organization' },
        {
          Connector: 'Generic FHIR R4',
          Resources: GENERIC_FHIR_SILENT_DEFAULT,
          'Retrieval resource type': GENERIC_FHIR_SILENT_DEFAULT,
        },
      );
      expect(findDestinationTypesOutsideSource(nodes, edges)).toBeNull();
    });

    it('skips a merge fed by one declaring and one legacy source, and checks it once both declare', () => {
      const nodes = [
        source('s1', { __name: 'Epic', ...declared, Resources: 'Patient' }),
        source('s2', { __name: 'athena', Connector: 'Athenahealth' }),
        transform('mg', 'merge'),
        transform('d1', 'dest-sqlserver', { dest_resources: 'Patient, Procedure' }),
      ];
      const edges = [edge('s1', 'mg'), edge('s2', 'mg'), edge('mg', 'd1')];
      expect(findDestinationTypesOutsideSource(nodes, edges)).toBeNull();

      const bothDeclare = nodes.map((n) =>
        n.id === 's2' ? source('s2', { __name: 'athena', ...declared, Resources: 'Condition' }) : n,
      );
      expect(findDestinationTypesOutsideSource(bothDeclare, edges)?.message).toBe(
        "Destination 'd1' writes Procedure, which its sources 'Epic' and 'athena' do not read. " +
          "Add it to the source's resource types or remove it from the destination.",
      );
    });

    it('passes when every type is read, and skips a legacy source with no declared list', () => {
      expect(findDestinationTypesOutsideSource(
        base({ dest_resources: 'Patient' }, { ...declared, Resources: 'Patient,Condition' }).nodes,
        [edge('s1', 'd1')],
      )).toBeNull();
      expect(findDestinationTypesOutsideSource(
        base({ dest_resources: 'Procedure' }, { Connector: 'Athenahealth' }).nodes,
        [edge('s1', 'd1')],
      )).toBeNull();
    });
  });
});
