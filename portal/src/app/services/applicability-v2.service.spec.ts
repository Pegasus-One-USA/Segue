import { TestBed } from '@angular/core/testing';
import { ApplicabilityServiceV2 } from './applicability-v2.service';
import { CanvasNode } from '../models/node-v2.model';

/**
 * Covers which nodes offer the `+` ("Add next module") button in V2's chain:
 * Source → Mapping → Transformation → De-identification → Destination.
 *
 * The De-identification case is a regression guard. It is the last chain step, so nothing can follow it —
 * but its `+` was shown whenever any step was still missing, and clicking it inserted that step UPSTREAM
 * (addableChainSteps always places in canonical order). The button pointed at a position nothing can go
 * into and then acted somewhere else.
 */
describe('ApplicabilityServiceV2.canAddNext', () => {
  let svc: ApplicabilityServiceV2;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [ApplicabilityServiceV2] });
    svc = TestBed.inject(ApplicabilityServiceV2);
  });

  const transform = (id: string, transformId: string): CanvasNode =>
    ({ id, kind: 'transform', transformId, fields: {}, x: 0, y: 0 }) as unknown as CanvasNode;

  const source = (id: string): CanvasNode =>
    ({ id, fields: { __name: 'Epic' }, x: 0, y: 0 }) as unknown as CanvasNode;

  const edge = (from: string, to: string) => ({ id: `${from}->${to}`, from, to });

  /** Source → Mapping → De-identification → Destination; Transformation is still missing. */
  function chainWithoutTransformation() {
    const nodes = [
      source('src'),
      transform('map', 'field-mapping'),
      transform('deid', 'deidentification'),
      transform('dest', 'dest-sqlserver'),
    ];
    const edges = [edge('src', 'map'), edge('map', 'deid'), edge('deid', 'dest')];
    return { nodes, edges };
  }

  it('hides the + on the De-identification node even when a step is still addable', () => {
    const { nodes, edges } = chainWithoutTransformation();
    const deid = nodes.find(n => (n as { transformId?: string }).transformId === 'deidentification')!;

    expect(svc.canAddNext(deid, nodes, edges)).toBe(false);
  });

  it('still offers Transformation from the Mapping node, so it stays reachable', () => {
    const { nodes, edges } = chainWithoutTransformation();
    const map = nodes.find(n => (n as { transformId?: string }).transformId === 'field-mapping')!;

    expect(svc.canAddNext(map, nodes, edges)).toBe(true);
    expect(svc.pickerModel(map, nodes, edges).items.map(i => i.id)).toContain('transformation');
  });

  it('hides the + on the destination, which terminates the pipeline', () => {
    const { nodes, edges } = chainWithoutTransformation();
    const dest = nodes.find(n => (n as { transformId?: string }).transformId === 'dest-sqlserver')!;

    expect(svc.canAddNext(dest, nodes, edges)).toBe(false);
  });
});
