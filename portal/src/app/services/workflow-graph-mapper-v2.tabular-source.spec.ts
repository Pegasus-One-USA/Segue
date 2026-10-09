import { TestBed } from '@angular/core/testing';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowApiService } from './workflow-api.service';
import { CanvasNode, SourceNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';

/**
 * The CSV / SQL source was one tile ('tabular') before it was split into "CSV file" and "SQL database". A node saved
 * before the split still carries __vendorId 'tabular': it reopens as the tile its tab_kind names.
 */
describe('WorkflowGraphMapperServiceV2 — a CSV / SQL source saved before the split', () => {
  let svc: WorkflowGraphMapperServiceV2;
  let loaded: { nodes: CanvasNode[]; edges: CanvasEdge[] };

  const reopen = (config: Record<string, string>): SourceNode => {
    svc.loadDefinition({
      id: 'wf',
      name: 'wf',
      nodes: [{
        id: 'src',
        nodeType: 'TabularSourceNode',
        category: 'Source',
        rank: 0,
        subRank: 0,
        displayName: 'CSV / SQL Table',
        configurationJson: JSON.stringify(config),
        positionX: 0,
        positionY: 0,
        isEnabled: true,
      }],
      edges: [],
    } as never);
    return loaded.nodes[0] as SourceNode;
  };

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

  it('reopens as the CSV file tile when tab_kind is csv', () => {
    const node = reopen({ __vendorId: 'tabular', __name: 'CSV / SQL Table', tab_kind: 'csv' });
    expect(node.vendorId).toBe('tabular-csv');
    expect(node.abbr).toBe('CSV');
  });

  it('reopens as the SQL database tile when tab_kind is sql', () => {
    const node = reopen({ __vendorId: 'tabular', __name: 'CSV / SQL Table', tab_kind: 'sql' });
    expect(node.vendorId).toBe('tabular-sql');
    expect(node.abbr).toBe('SQL');
  });
});
