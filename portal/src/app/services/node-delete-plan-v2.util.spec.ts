import { CanvasNode, SourceNode, TransformNode } from '../models/node-v2.model';
import { buildNodeDeletePlanV2, PlanEdge } from './node-delete-plan-v2.util';

/** Source → Mapping → Transformation → De-identification → Destination, V2's canvas order. */
function chainGraph(): { nodes: CanvasNode[]; edges: PlanEdge[] } {
  const source: SourceNode = {
    id: 'n1', x: 0, y: 0, connected: true, fields: { '__name': 'Epic' },
  };
  const mapping: TransformNode = {
    id: 'n2', kind: 'transform', transformId: 'field-mapping', x: 300, y: 0,
    fields: { '__name': 'Mapping' },
  };
  const transformation: TransformNode = {
    id: 'n3', kind: 'transform', transformId: 'transformation', x: 600, y: 0,
    fields: { '__name': 'Transformation' },
  };
  const deidentification: TransformNode = {
    id: 'n4', kind: 'transform', transformId: 'deidentification', x: 900, y: 0,
    fields: { '__name': 'De-identification' },
  };
  const destination: TransformNode = {
    id: 'n5', kind: 'transform', transformId: 'dest-sqlserver', x: 1200, y: 0,
    fields: {
      '__name': 'SQL Server',
      'dest_name': 'Warehouse',
      'dest_mappings': '[{"resource":"Patient","column":"Id"},{"resource":"Patient","column":"Name"}]',
      'dest_mappings_v2': '[{"resource":"Patient"},{"resource":"Patient"}]',
      'dest_mappingCount': '2',
      'dest_targets': '{"Patient":"dbo.Patient"}',
      'deIdentificationProfileId': 'profile-1',
      'destinationId': 'dest-guid',
      'dest_writeMode': 'upsert',
    },
  };

  return {
    nodes: [source, mapping, transformation, deidentification, destination],
    edges: [
      { id: 'e1', from: 'n1', to: 'n2' },
      { id: 'e2', from: 'n2', to: 'n3' },
      { id: 'e3', from: 'n3', to: 'n4' },
      { id: 'e4', from: 'n4', to: 'n5' },
    ],
  };
}

function removedIds(nodeId: string, graph = chainGraph()): string[] {
  return buildNodeDeletePlanV2(nodeId, graph.nodes, graph.edges)!.removals.map(r => r.id);
}

describe('buildNodeDeletePlanV2', () => {
  it('takes the whole chain when the destination goes', () => {
    expect(removedIds('n5')).toEqual(['n2', 'n3', 'n4', 'n5']);
  });

  it('leaves the destination in place and nothing to reconnect when it is deleted', () => {
    const graph = chainGraph();
    const plan = buildNodeDeletePlanV2('n5', graph.nodes, graph.edges)!;
    expect(plan.relink).toEqual([]);
    // Nothing to strip — the node holding the configuration is going too.
    expect(plan.fieldClears).toEqual([]);
  });

  it('takes Transformation and De-identification when Mapping goes, but not the destination', () => {
    expect(removedIds('n2')).toEqual(['n2', 'n3', 'n4']);
  });

  it('relinks the source straight to the destination after a Mapping delete', () => {
    const graph = chainGraph();
    const plan = buildNodeDeletePlanV2('n2', graph.nodes, graph.edges)!;
    expect(plan.relink).toEqual([{ from: 'n1', to: 'n5' }]);
  });

  it('strips the mapping and de-identification configuration off the surviving destination', () => {
    const graph = chainGraph();
    const plan = buildNodeDeletePlanV2('n2', graph.nodes, graph.edges)!;
    expect(plan.fieldClears.length).toBe(1);
    const clear = plan.fieldClears[0];
    expect(clear.nodeId).toBe('n5');
    expect(clear.keys).toContain('dest_mappings');
    expect(clear.keys).toContain('dest_mappings_v2');
    expect(clear.keys).toContain('dest_mappingCount');
    expect(clear.keys).toContain('dest_targets');
    expect(clear.keys).toContain('deIdentificationProfileId');
    // Connection identity and write behaviour belong to the destination, not to the removed steps.
    expect(clear.keys).not.toContain('destinationId');
    expect(clear.keys).not.toContain('dest_writeMode');
    expect(clear.keys).not.toContain('dest_name');
  });

  it('takes only itself when Transformation goes, healing the chain around it', () => {
    const graph = chainGraph();
    const plan = buildNodeDeletePlanV2('n3', graph.nodes, graph.edges)!;
    expect(plan.removals.map(r => r.id)).toEqual(['n3']);
    expect(plan.relink).toEqual([{ from: 'n2', to: 'n4' }]);
    expect(plan.fieldClears).toEqual([]);
  });

  it('takes only itself when De-identification goes, and clears its profile off the destination', () => {
    const graph = chainGraph();
    const plan = buildNodeDeletePlanV2('n4', graph.nodes, graph.edges)!;
    expect(plan.removals.map(r => r.id)).toEqual(['n4']);
    expect(plan.relink).toEqual([{ from: 'n3', to: 'n5' }]);
    expect(plan.fieldClears.length).toBe(1);
    expect(plan.fieldClears[0].keys).toEqual(['deIdentificationProfileId']);
  });

  it('reports transformation rules as a server-side leftover, since they are not on the canvas', () => {
    const graph = chainGraph();
    expect(buildNodeDeletePlanV2('n3', graph.nodes, graph.edges)!.serverSideLeftovers.length).toBe(1);
    // Not reported when the destination goes too — the whole pipeline is gone, so there is no
    // surviving module the rules could still be reached from.
    expect(buildNodeDeletePlanV2('n5', graph.nodes, graph.edges)!.serverSideLeftovers).toEqual([]);
  });

  it('takes the whole pipeline when the source goes', () => {
    expect(removedIds('n1')).toEqual(['n1', 'n2', 'n3', 'n4', 'n5']);
  });

  it('keeps what another source still feeds when one leg of a merge is deleted', () => {
    const graph = chainGraph();
    const second: SourceNode = {
      id: 'n6', x: 0, y: 200, connected: true, fields: { '__name': 'Athenahealth' },
    };
    graph.nodes.push(second);
    graph.edges.push({ id: 'e5', from: 'n6', to: 'n2' });

    // Deleting Epic must not take the chain Athenahealth is still feeding.
    const plan = buildNodeDeletePlanV2('n1', graph.nodes, graph.edges)!;
    expect(plan.removals.map(r => r.id)).toEqual(['n1']);
    expect(plan.relink).toEqual([]);
  });

  it('names the modules and their configuration for the confirmation', () => {
    const graph = chainGraph();
    const plan = buildNodeDeletePlanV2('n5', graph.nodes, graph.edges)!;
    expect(plan.targetLabel).toBe('SQL Server');
    expect(plan.removals.map(r => r.label))
      .toEqual(['Mapping', 'Transformation', 'De-identification', 'SQL Server']);
    expect(plan.removals[3].detail).toBe('Warehouse');
    expect(plan.removals[0].detail).toBe('2 mapped fields');
  });

  it('returns null for an unknown node', () => {
    const graph = chainGraph();
    expect(buildNodeDeletePlanV2('nope', graph.nodes, graph.edges)).toBeNull();
  });

  it('takes only itself for a plain transform node', () => {
    const nodes: CanvasNode[] = [
      { id: 'n1', x: 0, y: 0, connected: true, fields: {} } as SourceNode,
      { id: 'n2', kind: 'transform', transformId: 'terminology', x: 300, y: 0, fields: {} } as TransformNode,
      { id: 'n3', kind: 'transform', transformId: 'dest-csv', x: 600, y: 0, fields: {} } as TransformNode,
    ];
    const edges: PlanEdge[] = [
      { id: 'e1', from: 'n1', to: 'n2' },
      { id: 'e2', from: 'n2', to: 'n3' },
    ];
    const plan = buildNodeDeletePlanV2('n2', nodes, edges)!;
    expect(plan.removals.map(r => r.id)).toEqual(['n2']);
    expect(plan.relink).toEqual([{ from: 'n1', to: 'n3' }]);
  });
});
