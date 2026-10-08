import { TestBed } from '@angular/core/testing';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import {
  WorkflowApiService,
  WorkflowCatalogItem,
  WorkflowDefinitionDto,
  WorkflowDefinitionRequest,
  WorkflowNodeDto,
} from './workflow-api.service';
import { CanvasNode, MpiNode, SourceNode, TransformNode, isMpiNode, isTransformNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';
import { MPI_IDENTIFIERS_FIELD } from './mpi-node.util';

/**
 * The MPI step sits in the canvas chain (Source → MPI → …) but must not change what a run executes:
 * PatientMatchingNode is still a pass-through. So the saved graph routes around it, the MPI node itself is saved
 * DISABLED with no edges (every enabled-node pass on the server skips it), and it records where it sat so a
 * reload can put it back — across the fresh node ids the server issues on every save.
 */
describe('WorkflowGraphMapperServiceV2 — MPI node', () => {
  let svc: WorkflowGraphMapperServiceV2;
  let store: { nodes: () => CanvasNode[]; edges: () => CanvasEdge[]; loadGraph: jasmine.Spy };

  const catalog: WorkflowCatalogItem[] = [
    {
      nodeType: 'PatientMatchingNode', transformId: 'patient-matching', category: 10, rank: 33,
      displayName: 'Patient Matching (MPI)', requiredConfigurationFields: [],
      inputContracts: ['NormalizedResourceBatch'], outputContract: 'NormalizedResourceBatch', executorKey: 'patient-matching',
    },
  ];

  const source: SourceNode =
    { id: 'src', x: 360, y: 300, connected: true, vendorId: 'epic', fields: { '__name': 'Epic', 'Connector': 'Epic' } };
  const mpi: MpiNode = {
    id: 'mpi', kind: 'mpi', x: 660, y: 300,
    fields: { '__name': 'Master Patient Index', [MPI_IDENTIFIERS_FIELD]: 'names,dates,ssn' },
  };
  const destination = (transformId: string): TransformNode =>
    ({ id: 'dest', kind: 'transform', transformId, x: 960, y: 300, fields: { '__name': 'Destination' } });

  const edge = (from: string, to: string): CanvasEdge => ({ id: `${from}->${to}`, from, to });

  /** What the server hands back after saving `request`: new node ids every time (BuildWorkflow), the id the
   *  canvas sent stamped as `canvasNodeId` when there isn't one yet (StampNodeIdentity), edges re-pointed. */
  let saveCount = 0;
  function serverRoundTrip(request: WorkflowDefinitionRequest): WorkflowDefinitionDto {
    saveCount++;
    const newId = new Map(request.nodes.map((node, i) => [node.id, `srv${saveCount}-${i}`]));
    const nodes: WorkflowNodeDto[] = request.nodes.map(node => {
      const config = JSON.parse(node.configurationJson ?? '{}');
      config['canvasNodeId'] ??= node.id;
      return { ...node, id: newId.get(node.id)!, configurationJson: JSON.stringify(config) };
    });
    const edges = request.edges.map((e, i) => ({ id: `srv${saveCount}-e${i}`, fromNodeId: newId.get(e.fromNodeId)!, toNodeId: newId.get(e.toNodeId)! }));
    return { id: 'wf', name: 'wf', version: 1, isEnabled: true, nodes, edges };
  }

  function save(nodes: CanvasNode[], edges: CanvasEdge[]): WorkflowDefinitionRequest {
    store.nodes = () => nodes;
    store.edges = () => edges;
    return svc.toRequest('wf', null, null, null);
  }

  function load(definition: WorkflowDefinitionDto): { nodes: CanvasNode[]; edges: CanvasEdge[] } {
    svc.loadDefinition(definition);
    const [nodes, edges] = store.loadGraph.calls.mostRecent().args as [CanvasNode[], CanvasEdge[]];
    return { nodes, edges };
  }

  /** The canvas chain from the source, as node kinds/transform ids — follows the single path forward. */
  function chainFromSource(nodes: CanvasNode[], edges: CanvasEdge[]): string[] {
    const byId = new Map(nodes.map(node => [node.id, node]));
    let cursor: CanvasNode | undefined = nodes.find(node => !node.kind);
    const out: string[] = [];
    for (let guard = 0; cursor && guard < 10; guard++) {
      out.push(isMpiNode(cursor) ? 'mpi' : isTransformNode(cursor) ? cursor.transformId : 'source');
      const next = edges.filter(e => e.from === cursor!.id);
      expect(next.length).toBeLessThanOrEqual(1);
      cursor = next.length ? byId.get(next[0].to) : undefined;
    }
    return out;
  }

  beforeEach(() => {
    saveCount = 0;
    store = { nodes: () => [], edges: () => [], loadGraph: jasmine.createSpy('loadGraph') };
    TestBed.configureTestingModule({
      providers: [
        WorkflowGraphMapperServiceV2,
        { provide: PipelineStoreV2, useValue: store },
        { provide: WorkflowApiService, useValue: { catalog: () => catalog } },
      ],
    });
    svc = TestBed.inject(WorkflowGraphMapperServiceV2);
  });

  it('saves the MPI node as a disabled PatientMatchingNode with no edges', () => {
    const request = save([source, mpi, destination('dest-fhir')], [edge('src', 'mpi'), edge('mpi', 'dest')]);
    const saved = request.nodes.find(node => node.id === 'mpi')!;

    expect(saved.nodeType).toBe('PatientMatchingNode');
    expect(saved.isEnabled).toBeFalse();
    expect(JSON.parse(saved.configurationJson!)[MPI_IDENTIFIERS_FIELD]).toBe('names,dates,ssn');
    expect(request.edges.some(e => e.fromNodeId === 'mpi' || e.toNodeId === 'mpi')).toBeFalse();
    expect(request.nodes.filter(node => node.id !== 'mpi').every(node => node.isEnabled)).toBeTrue();
  });

  it('routes the run graph around the MPI — the source feeds what the MPI fed', () => {
    const request = save([source, mpi, destination('dest-fhir')], [edge('src', 'mpi'), edge('mpi', 'dest')]);
    expect(request.edges).toEqual([{ fromNodeId: 'src', toNodeId: 'dest' }]);
  });

  it('puts the MPI back between the source and its pipeline on reload', () => {
    const { nodes, edges } = load(serverRoundTrip(
      save([source, mpi, destination('dest-fhir')], [edge('src', 'mpi'), edge('mpi', 'dest')])));

    expect(chainFromSource(nodes, edges)).toEqual(['source', 'mpi', 'dest-fhir']);
    const restored = nodes.find(isMpiNode)!;
    expect(restored.fields[MPI_IDENTIFIERS_FIELD]).toBe('names,dates,ssn');
    expect(restored.fields['__mpiUpstream']).toBeUndefined();
  });

  it('keeps it in place across a second save, when every node id has changed again', () => {
    const first = load(serverRoundTrip(
      save([source, mpi, destination('dest-fhir')], [edge('src', 'mpi'), edge('mpi', 'dest')])));
    const second = load(serverRoundTrip(save(first.nodes, first.edges)));

    expect(chainFromSource(second.nodes, second.edges)).toEqual(['source', 'mpi', 'dest-fhir']);
  });

  it('threads it back in front of a synthetic Mapping node the save inserted for a SQL destination', () => {
    const request = save([source, mpi, destination('dest-sqlserver')], [edge('src', 'mpi'), edge('mpi', 'dest')]);
    // Saved run graph: Source → Mapping (synthetic) → SQL Server, with the MPI nowhere in it.
    expect(request.edges.map(e => `${e.fromNodeId}->${e.toNodeId}`)).toEqual(['src->dest__mapping', 'dest__mapping->dest']);

    const { nodes, edges } = load(serverRoundTrip(request));
    expect(chainFromSource(nodes, edges)).toEqual(['source', 'mpi', 'field-mapping', 'dest-sqlserver']);
  });

  it('routes every source feeding the MPI to its pipeline, and threads them all back in on reload', () => {
    const second: SourceNode = { ...source, id: 'src-2', y: 500, fields: { '__name': 'Athena', 'Connector': 'Athenahealth' } };
    const request = save(
      [source, second, mpi, destination('dest-sqlserver')],
      [edge('src', 'mpi'), edge('src-2', 'mpi'), edge('mpi', 'dest')],
    );
    expect(request.edges.map(e => `${e.fromNodeId}->${e.toNodeId}`).sort())
      .toEqual(['dest__mapping->dest', 'src->dest__mapping', 'src-2->dest__mapping'].sort());

    const { nodes, edges } = load(serverRoundTrip(request));
    const mpiId = nodes.find(isMpiNode)!.id;
    const feeding = edges.filter(e => e.to === mpiId).map(e => nodes.find(n => n.id === e.from)!.fields['canvasNodeId']);
    expect(feeding.sort()).toEqual(['src', 'src-2']);
    expect(edges.filter(e => e.from === mpiId).length).toBe(1);
    expect(edges.some(e => e.to !== mpiId && nodes.find(n => n.id === e.from && !n.kind))).toBeFalse();
  });

  it('leaves the MPI on the canvas, unconnected, when its recorded neighbours are gone', () => {
    const request = save([source, mpi], [edge('src', 'mpi')]);
    const definition = serverRoundTrip(request);
    definition.nodes = definition.nodes.filter(node => !JSON.parse(node.configurationJson!)['Connector']);

    const { nodes, edges } = load(definition);
    expect(nodes.some(isMpiNode)).toBeTrue();
    expect(edges).toEqual([]);
  });

  it('hands the saved MPI node the rules set on the MPI Rule tab of every destination it feeds', () => {
    const rows = [
      { resource: 'Patient', mode: 'value', sources: [{ fhirPath: 'Patient.identifier.value' }], tableName: 'dbo.Patient', targetName: 'Identifier', isMpiMatch: true },
      { resource: 'Patient', mode: 'value', sources: [{ fhirPath: 'Patient.name.given' }, { fhirPath: 'Patient.name.family' }], tableName: 'dbo.Patient', targetName: 'FullName', isMpiMatch: true },
      { resource: 'Patient', mode: 'value', sources: [{ fhirPath: 'Patient.gender' }], tableName: 'dbo.Patient', targetName: 'Gender' },
      // A fixed value carries no source to match on, even with a stray flag.
      { resource: 'Patient', mode: 'default', sources: [], tableName: 'dbo.Patient', targetName: 'Origin', isMpiMatch: true },
    ];
    const fed: TransformNode = {
      ...destination('dest-sqlserver'),
      fields: {
        __name: 'SQL',
        dest_mappings_v2: JSON.stringify([
          ...rows,
          // Observation keeps the default thresholds — none were set for it.
          { resource: 'Observation', mode: 'value', sources: [{ fhirPath: 'Observation.subject.reference' }], tableName: 'dbo.Obs', targetName: 'Subject', isMpiMatch: true },
        ]),
        dest_mpiThresholds: JSON.stringify({ Patient: { autoApprove: 95, manualReview: 75 } }),
      },
    };
    // Another pipeline's destination, which the MPI doesn't feed — its choice is not the MPI's business.
    const other: TransformNode = {
      ...destination('dest-sqlserver'), id: 'other',
      fields: { __name: 'Other', dest_mappings_v2: JSON.stringify([{ ...rows[0], targetName: 'Elsewhere' }]) },
    };
    const otherSource: SourceNode = { ...source, id: 'src-2' };

    const request = save(
      [source, otherSource, mpi, fed, other],
      [edge('src', 'mpi'), edge('mpi', 'dest'), edge('src-2', 'other')],
    );
    const config = JSON.parse(request.nodes.find(node => node.id === 'mpi')!.configurationJson!);

    expect(JSON.parse(config['mpiRules'])).toEqual([
      {
        destination: 'dest', resourceType: 'Patient', autoApprovePercent: 95, manualReviewPercent: 75,
        fields: [
          { sourceFields: ['Patient.identifier.value'], table: 'dbo.Patient', column: 'Identifier' },
          { sourceFields: ['Patient.name.given', 'Patient.name.family'], table: 'dbo.Patient', column: 'FullName' },
        ],
      },
      {
        destination: 'dest', resourceType: 'Observation', autoApprovePercent: 90, manualReviewPercent: 70,
        fields: [{ sourceFields: ['Observation.subject.reference'], table: 'dbo.Obs', column: 'Subject' }],
      },
    ]);
  });

  it('does not carry the saved rules onto the canvas — they are rebuilt on every save', () => {
    const request = save([source, mpi, destination('dest-fhir')], [edge('src', 'mpi'), edge('mpi', 'dest')]);
    const { nodes } = load(serverRoundTrip(request));
    expect(nodes.find(isMpiNode)!.fields['mpiRules']).toBeUndefined();
  });

  it('still loads a V1 patient-matching step (same node type, no MPI marker) as a transform', () => {
    const { nodes } = load({
      id: 'wf', name: 'wf', version: 1, isEnabled: true, edges: [],
      nodes: [{
        id: 'pm', nodeType: 'PatientMatchingNode', category: 10, rank: 33, subRank: 0, displayName: null,
        configurationJson: JSON.stringify({ __transformId: 'patient-matching' }), positionX: 0, positionY: 0, isEnabled: true,
      }],
    });
    expect(isTransformNode(nodes[0])).toBeTrue();
  });
});
