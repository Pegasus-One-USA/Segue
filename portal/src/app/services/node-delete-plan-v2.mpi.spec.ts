import { CanvasNode, MpiNode, SourceNode, TransformNode } from '../models/node-v2.model';
import { buildNodeDeletePlanV2, PlanEdge } from './node-delete-plan-v2.util';
import { MPI_IDENTIFIERS_FIELD } from './mpi-node.util';

const source: SourceNode = { id: 'src', x: 360, y: 300, connected: true, fields: { '__name': 'Epic' } };

const mpi = (identifiers = ''): MpiNode =>
  ({ id: 'mpi', kind: 'mpi', x: 660, y: 300, fields: { '__name': 'Master Patient Index', [MPI_IDENTIFIERS_FIELD]: identifiers } });

const destination: TransformNode =
  { id: 'dest', kind: 'transform', transformId: 'dest-csv', x: 960, y: 300, fields: { '__name': 'CSV' } };

/** Source → MPI → CSV. */
function graph(identifiers = ''): { nodes: CanvasNode[]; edges: PlanEdge[] } {
  return {
    nodes: [source, mpi(identifiers), destination],
    edges: [{ id: 'e1', from: 'src', to: 'mpi' }, { id: 'e2', from: 'mpi', to: 'dest' }],
  };
}

describe('buildNodeDeletePlanV2 — MPI node', () => {
  it('removes only the MPI and reconnects the source to what it fed', () => {
    const { nodes, edges } = graph('names,ssn');
    const plan = buildNodeDeletePlanV2('mpi', nodes, edges)!;

    expect(plan.removals.map(removal => removal.id)).toEqual(['mpi']);
    expect(plan.removals[0].detail).toBe('2 selected identifiers');
    expect(plan.relink).toEqual([{ from: 'src', to: 'dest' }]);
  });

  it('goes with its source, like the rest of that source’s pipeline', () => {
    const { nodes, edges } = graph();
    const plan = buildNodeDeletePlanV2('src', nodes, edges)!;
    expect(plan.removals.map(removal => removal.id)).toEqual(['src', 'mpi', 'dest']);
  });

  it('stays when its destination is deleted, so a new one can be added from its `+`', () => {
    const { nodes, edges } = graph();
    const plan = buildNodeDeletePlanV2('dest', nodes, edges)!;
    expect(plan.removals.map(removal => removal.id)).toEqual(['dest']);
  });
});
