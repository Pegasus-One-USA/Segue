import { TestBed } from '@angular/core/testing';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowApiService } from './workflow-api.service';
import { CanvasNode, TransformNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';

/**
 * A transform/destination node's subtitle reads "from <source name>" (transform-node.component.html). When a node
 * is added on the canvas the builder stamps sourceName from its root source, but a reopened workflow is rebuilt by
 * loadDefinition — which must resolve the same name, or every reopened node reads a bare "from".
 */
describe('WorkflowGraphMapperServiceV2 — sourceName on load', () => {
  let svc: WorkflowGraphMapperServiceV2;
  let loaded: { nodes: CanvasNode[]; edges: CanvasEdge[] };

  const dtoNode = (id: string, category: number, config: Record<string, string>) => ({
    id,
    nodeType: id,
    category,
    rank: 0,
    subRank: 0,
    displayName: config['__name'],
    configurationJson: JSON.stringify(config),
    positionX: 0,
    positionY: 0,
    isEnabled: true,
  });

  beforeEach(() => {
    loaded = { nodes: [], edges: [] };
    TestBed.configureTestingModule({
      providers: [
        WorkflowGraphMapperServiceV2,
        {
          provide: PipelineStoreV2,
          useValue: { loadGraph: (nodes: CanvasNode[], edges: CanvasEdge[]) => (loaded = { nodes, edges }) },
        },
        { provide: WorkflowApiService, useValue: { catalog: () => [] } },
      ],
    });
    svc = TestBed.inject(WorkflowGraphMapperServiceV2);
  });

  it('names every downstream node after its root source', () => {
    svc.loadDefinition({
      id: 'wf',
      name: 'wf',
      nodes: [
        dtoNode('src', 0, { __name: 'BackendSystem2Oct2026' }),
        dtoNode('map', 1, { __name: 'Mapping', __transformId: 'field-mapping' }),
        dtoNode('api', 1, { __name: 'API Endpoint', __transformId: 'api-endpoint' }),
      ],
      edges: [
        { id: 'e1', fromNodeId: 'src', toNodeId: 'map' },
        { id: 'e2', fromNodeId: 'map', toNodeId: 'api' },
      ],
    } as never);

    const sourceNames = loaded.nodes
      .filter((node): node is TransformNode => node.kind === 'transform')
      .map(node => node.sourceName);

    expect(sourceNames).toEqual(['BackendSystem2Oct2026', 'BackendSystem2Oct2026']);
  });
});
