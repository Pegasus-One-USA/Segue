import { TestBed } from '@angular/core/testing';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowApiService, WorkflowDefinitionDto, WorkflowDefinitionRequest, WorkflowNodeDto } from './workflow-api.service';
import { CanvasNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';

/**
 * Several sources feeding one chain — through the MPI step, or with their ports dragged onto it. The save reorders
 * the chain into execution order (Transformation → De-identification → Mapping → Destination) and used to give up
 * on any step with more than one input, which left the sources wired straight to Mapping: every record skipped
 * Transformation AND De-identification and reached the destination un-redacted, and the reopened canvas showed the
 * MPI cut off from its chain.
 */
describe('WorkflowGraphMapperServiceV2 — several sources into one chain', () => {
  let svc: WorkflowGraphMapperServiceV2;
  let store: { nodes: () => CanvasNode[]; edges: () => CanvasEdge[]; loadGraph: jasmine.Spy };
  let saveCount = 0;

  const source = (id: string): CanvasNode =>
    ({ id, x: 0, y: 0, connected: true, vendorId: 'epic', fields: { __name: id, Connector: 'Epic' } }) as CanvasNode;
  const step = (id: string, transformId: string): CanvasNode =>
    ({ id, kind: 'transform', transformId, x: 0, y: 0, fields: { __name: id } }) as CanvasNode;
  const mpi: CanvasNode = { id: 'MPI', kind: 'mpi', x: 0, y: 0, fields: { __name: 'Master Patient Index' } };
  const chain = (): CanvasNode[] => [
    step('MAP', 'field-mapping'), step('TRA', 'transformation'), step('DEID', 'deidentification'), step('SQL', 'dest-sqlserver'),
  ];
  const edges = (...pairs: [string, string][]): CanvasEdge[] => pairs.map(([from, to]) => ({ id: `${from}-${to}`, from, to }));
  const authoredChain: [string, string][] = [['MAP', 'TRA'], ['TRA', 'DEID'], ['DEID', 'SQL']];

  /** Edges as "from->to" in canvas ids (stable across saves via canvasNodeId), sorted. */
  const describeEdges = (nodes: CanvasNode[], list: CanvasEdge[]): string[] => {
    const key = (id: string) => nodes.find(node => node.id === id)?.fields['canvasNodeId'] ?? id;
    return list.map(edge => `${key(edge.from)}->${key(edge.to)}`).sort();
  };
  const describeRunGraph = (request: WorkflowDefinitionRequest): string[] =>
    request.edges.map(edge => `${edge.fromNodeId}->${edge.toNodeId}`).sort();

  function serverRoundTrip(request: WorkflowDefinitionRequest): WorkflowDefinitionDto {
    saveCount++;
    const newId = new Map(request.nodes.map((node, i) => [node.id, `srv${saveCount}-${i}`]));
    const nodes: WorkflowNodeDto[] = request.nodes.map(node => {
      const config = JSON.parse(node.configurationJson ?? '{}');
      config['canvasNodeId'] ??= node.id;
      return { ...node, id: newId.get(node.id)!, configurationJson: JSON.stringify(config) };
    });
    const saved = request.edges.map((e, i) => ({ id: `srv${saveCount}-e${i}`, fromNodeId: newId.get(e.fromNodeId)!, toNodeId: newId.get(e.toNodeId)! }));
    return { id: 'wf', name: 'wf', version: 1, isEnabled: true, nodes, edges: saved };
  }

  function save(nodes: CanvasNode[], list: CanvasEdge[]): WorkflowDefinitionRequest {
    store.nodes = () => nodes;
    store.edges = () => list;
    return svc.toRequest('wf', null, null, null);
  }

  function load(definition: WorkflowDefinitionDto): { nodes: CanvasNode[]; edges: CanvasEdge[] } {
    svc.loadDefinition(definition);
    const [nodes, list] = store.loadGraph.calls.mostRecent().args as [CanvasNode[], CanvasEdge[]];
    return { nodes, edges: list };
  }

  beforeEach(() => {
    saveCount = 0;
    store = { nodes: () => [], edges: () => [], loadGraph: jasmine.createSpy('loadGraph') };
    TestBed.configureTestingModule({
      providers: [
        WorkflowGraphMapperServiceV2,
        { provide: PipelineStoreV2, useValue: store },
        { provide: WorkflowApiService, useValue: { catalog: () => [] } },
      ],
    });
    svc = TestBed.inject(WorkflowGraphMapperServiceV2);
  });

  const mpiWorkflow = () => ({
    nodes: [source('EP'), source('ATH'), source('ECW'), mpi, ...chain()],
    edges: edges(['EP', 'MPI'], ['ATH', 'MPI'], ['ECW', 'MPI'], ['MPI', 'MAP'], ...authoredChain),
  });

  it('runs every source through the whole chain when several feed the MPI', () => {
    const { nodes, edges: list } = mpiWorkflow();
    expect(describeRunGraph(save(nodes, list))).toEqual([
      'ATH->TRA', 'DEID->MAP', 'ECW->TRA', 'EP->TRA', 'MAP->SQL', 'TRA->DEID',
    ]);
  });

  it('reopens it with the MPI in front of the chain, and keeps it there across another save', () => {
    const { nodes, edges: list } = mpiWorkflow();
    const expected = ['ATH->MPI', 'DEID->SQL', 'ECW->MPI', 'EP->MPI', 'MAP->TRA', 'MPI->MAP', 'TRA->DEID'];

    const first = load(serverRoundTrip(save(nodes, list)));
    expect(describeEdges(first.nodes, first.edges)).toEqual(expected);

    const second = load(serverRoundTrip(save(first.nodes, first.edges)));
    expect(describeEdges(second.nodes, second.edges)).toEqual(expected);
  });

  it('repairs a workflow saved by the old walk, with the sources wired straight to Mapping', () => {
    const { nodes, edges: list } = mpiWorkflow();
    const definition = serverRoundTrip(save(nodes, list));
    const idOf = (canvasId: string) =>
      definition.nodes.find(node => JSON.parse(node.configurationJson!)['canvasNodeId'] === canvasId)!.id;
    // Exactly what that save persisted: Transformation fed by nothing, every source into Mapping.
    definition.edges = [['EP', 'MAP'], ['ATH', 'MAP'], ['ECW', 'MAP'], ['TRA', 'DEID'], ['DEID', 'MAP'], ['MAP', 'SQL']]
      .map(([from, to], i) => ({ id: `old-${i}`, fromNodeId: idOf(from), toNodeId: idOf(to) }));

    const reopened = load(definition);
    expect(describeEdges(reopened.nodes, reopened.edges)).toEqual(
      ['ATH->MPI', 'DEID->SQL', 'ECW->MPI', 'EP->MPI', 'MAP->TRA', 'MPI->MAP', 'TRA->DEID']);
  });

  it('runs every source through the whole chain when their ports are dragged onto it, no MPI involved', () => {
    const nodes = [source('EP'), source('ATH'), ...chain()];
    const list = edges(['EP', 'MAP'], ['ATH', 'MAP'], ...authoredChain);
    expect(describeRunGraph(save(nodes, list))).toEqual(['ATH->TRA', 'DEID->MAP', 'EP->TRA', 'MAP->SQL', 'TRA->DEID']);
  });

  it('leaves a single-source chain exactly as it was', () => {
    const nodes = [source('EP'), ...chain()];
    const list = edges(['EP', 'MAP'], ...authoredChain);
    expect(describeRunGraph(save(nodes, list))).toEqual(['DEID->MAP', 'EP->TRA', 'MAP->SQL', 'TRA->DEID']);

    const reopened = load(serverRoundTrip(save(nodes, list)));
    expect(describeEdges(reopened.nodes, reopened.edges)).toEqual(['DEID->SQL', 'EP->MAP', 'MAP->TRA', 'TRA->DEID']);
  });
});
