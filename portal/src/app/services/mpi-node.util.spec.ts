import { CanvasNode, MpiNode, SourceNode } from '../models/node-v2.model';
import { MPI_IDENTIFIERS } from '../data/mpi-identifiers.data';
import {
  MPI_IDENTIFIERS_FIELD,
  hasMpiNode,
  mpiIdentifierIds,
  mpiThresholdErrors,
  mpiThresholdsFor,
  serializeMpiIdentifiers,
} from './mpi-node.util';

const source = (id: string): SourceNode =>
  ({ id, x: 360, y: 300, connected: true, fields: { '__name': id } });

const mpi = (identifiers = ''): MpiNode =>
  ({ id: 'mpi', kind: 'mpi', x: 660, y: 300, fields: { [MPI_IDENTIFIERS_FIELD]: identifiers } });

describe('hasMpiNode', () => {
  it('is false for a canvas without one', () => {
    expect(hasMpiNode([source('a'), source('b')])).toBeFalse();
  });

  it('is true once one is on the canvas', () => {
    const nodes: CanvasNode[] = [source('a'), mpi()];
    expect(hasMpiNode(nodes)).toBeTrue();
  });
});

describe('MPI identifier (de)serialization', () => {
  it('reads ids back in catalog order and drops unknown ones', () => {
    expect(mpiIdentifierIds(mpi('ssn, names,not-a-real-identifier,,mrn'))).toEqual(['names', 'ssn', 'mrn']);
  });

  it('serializes in catalog order regardless of selection order', () => {
    expect(serializeMpiIdentifiers(['mrn', 'names', 'ssn'])).toBe('names,ssn,mrn');
  });

  it('round-trips the full catalog of 18 identifiers', () => {
    const all = MPI_IDENTIFIERS.map(identifier => identifier.id);
    expect(all.length).toBe(18);
    expect(mpiIdentifierIds(mpi(serializeMpiIdentifiers(all)))).toEqual(all);
  });
});

describe('MPI score thresholds', () => {
  it('accepts the documented example: auto-approve at 90%, manual review from 70%', () => {
    expect(mpiThresholdErrors({ autoApprove: 90, manualReview: 70 })).toEqual({});
  });

  it('accepts the edges of the scale', () => {
    expect(mpiThresholdErrors({ autoApprove: 100, manualReview: 0 })).toEqual({});
  });

  it('needs manual review to start strictly below auto-approve, so the bands never overlap', () => {
    expect(mpiThresholdErrors({ autoApprove: 80, manualReview: 80 }).manualReview)
      .toBe('Manual review must start below auto-approve (80%).');
    expect(mpiThresholdErrors({ autoApprove: 70, manualReview: 90 }).manualReview).toBeDefined();
  });

  it('rejects an empty, fractional or out-of-range percentage, field by field', () => {
    expect(mpiThresholdErrors({ autoApprove: null, manualReview: 70 })).toEqual({ autoApprove: 'Enter a whole number from 0 to 100.' });
    expect(mpiThresholdErrors({ autoApprove: 90, manualReview: 70.5 })).toEqual({ manualReview: 'Enter a whole number from 0 to 100.' });
    expect(mpiThresholdErrors({ autoApprove: 101, manualReview: -1 })).toEqual({
      autoApprove: 'Enter a whole number from 0 to 100.',
      manualReview: 'Enter a whole number from 0 to 100.',
    });
  });

  it('reads a resource\'s saved thresholds off a destination, defaulting to 90/70', () => {
    const fields = { dest_mpiThresholds: JSON.stringify({ Patient: { autoApprove: 95, manualReview: 60 } }) };
    expect(mpiThresholdsFor(fields, 'Patient')).toEqual({ autoApprove: 95, manualReview: 60 });
    expect(mpiThresholdsFor(fields, 'Practitioner')).toEqual({ autoApprove: 90, manualReview: 70 });
    expect(mpiThresholdsFor({ dest_mpiThresholds: 'not json' }, 'Patient')).toEqual({ autoApprove: 90, manualReview: 70 });
  });
});
