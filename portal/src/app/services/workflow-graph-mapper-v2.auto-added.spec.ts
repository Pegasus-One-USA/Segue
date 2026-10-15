import { TestBed } from '@angular/core/testing';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowApiService } from './workflow-api.service';
import { CanvasNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';

/** __autoAdded marks a chain step the builder added for rules, the only kind it may remove again. It must survive a
 *  save and a reload — otherwise every reopened workflow would forget which steps are the builder's — and a step the
 *  user has claimed (marker removed) must stay claimed across the same round trip. */
describe('WorkflowGraphMapperServiceV2 — the __autoAdded marker across save and reload', () => {
  let svc: WorkflowGraphMapperServiceV2;
  let store: { nodes: () => CanvasNode[]; edges: () => CanvasEdge[]; loadGraph: (n: CanvasNode[], e: CanvasEdge[]) => void };
  let loaded: CanvasNode[] = [];

  const step = (id: string, fields: Record<string, string>): CanvasNode =>
    ({ id, kind: 'transform', transformId: 'transformation', x: 0, y: 0, fields: { __name: 'Transformation', ...fields } }) as unknown as CanvasNode;

  beforeEach(() => {
    store = { nodes: () => [], edges: () => [], loadGraph: nodes => { loaded = nodes; } };
    TestBed.configureTestingModule({
      providers: [
        WorkflowGraphMapperServiceV2,
        { provide: PipelineStoreV2, useValue: store },
        { provide: WorkflowApiService, useValue: { catalog: () => [] } },
      ],
    });
    svc = TestBed.inject(WorkflowGraphMapperServiceV2);
  });

  it('is saved with a builder-added step, not with a claimed one, and both come back the same on reload', () => {
    const source = { id: 'src', x: 0, y: 0, fields: { __name: 'ECW' } } as unknown as CanvasNode;
    store.nodes = () => [source, step('auto', { __autoAdded: 'true' }), step('claimed', {})];
    store.edges = () => [{ id: 'e1', from: 'src', to: 'auto' }, { id: 'e2', from: 'auto', to: 'claimed' }] as CanvasEdge[];

    const request = svc.toRequest('wf', null, 'wf-1', null);
    const saved = (id: string) => JSON.parse(request.nodes.find((n: { id: string }) => n.id === id)!.configurationJson ?? '{}');
    expect(saved('auto')['__autoAdded']).toBe('true');
    expect(saved('claimed')['__autoAdded']).toBeUndefined();

    svc.loadDefinition({ id: 'wf-1', name: 'wf', nodes: request.nodes, edges: [] } as never);

    expect(loaded.find(n => n.id === 'auto')!.fields['__autoAdded']).toBe('true');
    expect(loaded.find(n => n.id === 'claimed')!.fields['__autoAdded']).toBeUndefined();
  });
});
