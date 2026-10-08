import { TestBed } from '@angular/core/testing';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { MpiNode, SourceNode, TransformNode, isMpiNode } from '../models/node-v2.model';
import { MPI_RANK } from '../models/transform-v2.model';

const source = (id: string): SourceNode =>
  ({ id, x: 360, y: 300, connected: true, fields: { '__name': id } });

const mpi: MpiNode = { id: 'mpi', kind: 'mpi', x: 660, y: 300, fields: {} };

const destination: TransformNode =
  { id: 'dest', kind: 'transform', transformId: 'dest-csv', x: 960, y: 300, fields: {} };

describe('PipelineStoreV2 — MPI node', () => {
  let store: PipelineStoreV2;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    store = TestBed.inject(PipelineStoreV2);
    store.reset();
  });

  it('never adds an MPI node by itself — it is optional', () => {
    store.addNode(source('a'));
    store.addNode(source('b'));
    expect(store.nodes().some(isMpiNode)).toBeFalse();
  });

  it('ranks the MPI after a source and before a destination, so ports connect in that direction only', () => {
    expect(store.nodeRank(mpi)).toBe(MPI_RANK);
    expect(store.nodeRank(source('a'))).toBeLessThan(MPI_RANK);
    expect(store.nodeRank(destination)).toBeGreaterThan(MPI_RANK);
  });

  it('resolves a destination behind the MPI to its real source', () => {
    store.loadGraph([source('a'), mpi, destination], [
      { id: 'e1', from: 'a', to: 'mpi' },
      { id: 'e2', from: 'mpi', to: 'dest' },
    ]);
    expect(store.rootSourceOf(destination)?.id).toBe('a');
  });
});
