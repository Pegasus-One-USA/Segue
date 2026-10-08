import { TestBed } from '@angular/core/testing';
import { ApplicabilityServiceV2 } from './applicability-v2.service';
import { CanvasNode, MpiNode, SourceNode, TransformNode } from '../models/node-v2.model';
import { CanvasEdge } from '../models/edge-v2.model';

const source = (id: string): SourceNode =>
  ({ id, x: 360, y: 300, connected: true, fields: { '__name': `Source ${id}` } });

const mpi: MpiNode = { id: 'mpi', kind: 'mpi', x: 660, y: 300, fields: { '__name': 'Master Patient Index' } };

const destination = (id: string, transformId = 'dest-sqlserver'): TransformNode =>
  ({ id, kind: 'transform', transformId, x: 960, y: 300, fields: { '__name': id } });

describe('ApplicabilityServiceV2 — MPI', () => {
  let svc: ApplicabilityServiceV2;
  const itemIds = (node: CanvasNode, nodes: CanvasNode[], edges: CanvasEdge[] = []) =>
    svc.pickerModel(node, nodes, edges).items.map(item => item.id);

  beforeEach(() => {
    TestBed.configureTestingModule({});
    svc = TestBed.inject(ApplicabilityServiceV2);
  });

  it('offers the MPI first from a bare source, ahead of the destinations', () => {
    const ids = itemIds(source('a'), [source('a')]);
    expect(ids[0]).toBe('mpi');
    expect(ids).toContain('dest-sqlserver');
  });

  it('offers the MPI from a source that already has a destination, alongside the chain steps', () => {
    const nodes = [source('a'), destination('d')];
    const ids = itemIds(nodes[0], nodes, [{ id: 'e1', from: 'a', to: 'd' }]);
    expect(ids[0]).toBe('mpi');
    expect(ids).toContain('field-mapping');
  });

  it('offers a second, bare source the existing MPI to connect to — still one MPI per workflow', () => {
    const nodes: CanvasNode[] = [source('a'), mpi, source('b')];
    const model = svc.pickerModel(nodes[2], nodes, [{ id: 'e1', from: 'a', to: 'mpi' }]);
    const row = model.items.find(item => item.id === 'mpi');
    expect(row).toBeDefined();
    expect(row!.reason).toBe('Connect to this workflow’s MPI');
  });

  it('does not offer it to the source already feeding the MPI', () => {
    const nodes: CanvasNode[] = [source('a'), mpi];
    expect(itemIds(nodes[0], nodes, [{ id: 'e1', from: 'a', to: 'mpi' }])).not.toContain('mpi');
  });

  it('does not offer it to a source that already has its own pipeline', () => {
    const nodes: CanvasNode[] = [source('a'), mpi, source('b'), destination('d')];
    const edges = [{ id: 'e1', from: 'a', to: 'mpi' }, { id: 'e2', from: 'b', to: 'd' }];
    expect(itemIds(nodes[2], nodes, edges)).not.toContain('mpi');
  });

  it('gives a bare MPI node the destinations to add next', () => {
    const nodes: CanvasNode[] = [source('a'), mpi];
    const edges = [{ id: 'e1', from: 'a', to: 'mpi' }];
    const ids = itemIds(mpi, nodes, edges);
    expect(ids).toContain('dest-sqlserver');
    expect(ids).not.toContain('mpi');
    expect(svc.canAddNext(mpi, nodes, edges)).toBeTrue();
  });

  it('gives an MPI with a destination the chain steps its pipeline is still missing', () => {
    const nodes: CanvasNode[] = [source('a'), mpi, destination('d')];
    const edges = [{ id: 'e1', from: 'a', to: 'mpi' }, { id: 'e2', from: 'mpi', to: 'd' }];
    expect(itemIds(mpi, nodes, edges)).toEqual(['field-mapping', 'transformation', 'deidentification']);
  });

  it('still resolves the destination through the MPI for the source’s own `+`', () => {
    const nodes: CanvasNode[] = [source('a'), mpi, destination('d')];
    const edges = [{ id: 'e1', from: 'a', to: 'mpi' }, { id: 'e2', from: 'mpi', to: 'd' }];
    expect(svc.destinationFor(nodes[0], nodes, edges)?.id).toBe('d');
    expect(itemIds(nodes[0], nodes, edges)).toContain('transformation');
  });
});
