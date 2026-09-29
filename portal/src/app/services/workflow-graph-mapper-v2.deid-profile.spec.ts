import { TestBed } from '@angular/core/testing';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowApiService } from './workflow-api.service';
import { CanvasNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';

/**
 * A de-identification policy belongs to the WORKFLOW, not to the destination connection — a connection can be
 * reused by several workflows, and DestinationConfiguration.DeIdentificationProfileId is a single column that
 * whichever workflow saved last would win. The policy therefore rides on the De-identification node itself,
 * because DeIdentificationNodeExecutor.ResolveProfileIdAsync reads node `profileId` before falling back to the
 * destination's column, and before falling back again to the seeded Safe Harbor default.
 *
 * Without this stamping the node carries nothing, the run silently redacts under whatever that shared column
 * happens to hold — or under a default policy nobody chose.
 */
describe('WorkflowGraphMapperServiceV2 — de-identification policy stamping', () => {
  let svc: WorkflowGraphMapperServiceV2;
  let store: { nodes: () => CanvasNode[]; edges: () => CanvasEdge[] };

  const WORKFLOW_ID = 'cf320a55-d619-4a76-866a-34e7e16098c3';
  const DESTINATION_ID = 'd1111111-1111-1111-1111-111111111111';
  const POLICY_ID = 'p2222222-2222-2222-2222-222222222222';

  const source = (): CanvasNode =>
    ({ id: 'src', fields: { __name: 'Epic' }, x: 0, y: 0 }) as unknown as CanvasNode;

  const chain = (id: string, transformId: string): CanvasNode =>
    ({ id, kind: 'transform', transformId, fields: { __name: transformId }, x: 0, y: 0 }) as unknown as CanvasNode;

  const destination = (profileId?: string): CanvasNode =>
    ({
      id: 'dest',
      kind: 'destination',
      transformId: 'sql-server',
      fields: {
        __name: 'SQL Server',
        destinationId: DESTINATION_ID,
        ...(profileId ? { deIdentificationProfileId: profileId } : {}),
      },
      x: 0, y: 0,
    }) as unknown as CanvasNode;

  const edge = (from: string, to: string): CanvasEdge =>
    ({ id: `${from}->${to}`, from, to }) as unknown as CanvasEdge;

  function build(nodes: CanvasNode[], edges: CanvasEdge[]) {
    store.nodes = () => nodes;
    store.edges = () => edges;
    return svc.toRequest('wf', null, WORKFLOW_ID, null);
  }

  /** The config the mapper emitted for whichever node has this nodeType. */
  function configOf(request: ReturnType<WorkflowGraphMapperServiceV2['toRequest']>, nodeType: string) {
    const node = request.nodes.find((n: { nodeType: string }) => n.nodeType === nodeType);
    return node ? JSON.parse(node.configurationJson ?? '{}') : undefined;
  }

  beforeEach(() => {
    store = { nodes: () => [], edges: () => [] };
    TestBed.configureTestingModule({
      providers: [
        WorkflowGraphMapperServiceV2,
        { provide: PipelineStoreV2, useValue: store },
        { provide: WorkflowApiService, useValue: { catalog: () => [] } },
      ],
    });
    svc = TestBed.inject(WorkflowGraphMapperServiceV2);
  });

  it('copies the workflow policy onto the De-identification node', () => {
    const request = build(
      [source(), chain('deid', 'deidentification'), destination(POLICY_ID)],
      [edge('src', 'deid'), edge('deid', 'dest')],
    );

    expect(configOf(request, 'DeIdentificationNode')?.['profileId']).toBe(POLICY_ID);
  });

  it('leaves the node without a policy when the workflow has none', () => {
    const request = build(
      [source(), chain('deid', 'deidentification'), destination()],
      [edge('src', 'deid'), edge('deid', 'dest')],
    );

    expect(configOf(request, 'DeIdentificationNode')?.['profileId']).toBeUndefined();
  });

  it('does not put a policy on the other chain nodes', () => {
    // Mapping and Transformation resolve their own rules; a profileId there would mean nothing and, worse,
    // would read as "this node de-identifies".
    const request = build(
      [source(), chain('tr', 'transformation'), chain('deid', 'deidentification'), chain('fm', 'field-mapping'), destination(POLICY_ID)],
      [edge('src', 'tr'), edge('tr', 'deid'), edge('deid', 'fm'), edge('fm', 'dest')],
    );

    expect(configOf(request, 'MappingNode')?.['profileId']).toBeUndefined();
    expect(configOf(request, 'FhirResourceTransformNode')?.['profileId']).toBeUndefined();
    expect(configOf(request, 'DeIdentificationNode')?.['profileId']).toBe(POLICY_ID);
  });

  it('still stamps destinationId and the workflow id on the chain', () => {
    // The policy stamping shares the same walk-back loop, so a regression there would break these too.
    const request = build(
      [source(), chain('deid', 'deidentification'), destination(POLICY_ID)],
      [edge('src', 'deid'), edge('deid', 'dest')],
    );

    const config = configOf(request, 'DeIdentificationNode');
    expect(config?.['destinationId']).toBe(DESTINATION_ID);
    expect(config?.['resourcePipelineRouteId']).toBe(WORKFLOW_ID);
  });
});
