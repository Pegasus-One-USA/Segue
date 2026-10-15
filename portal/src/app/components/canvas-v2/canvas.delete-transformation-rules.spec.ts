import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { CanvasComponent } from './canvas.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { ToastService } from '../../services/toast.service';
import { PermissionService } from '../../auth/services/permission.service';
import { TransformationRulesService } from '../node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { CanvasNode } from '../../models/node-v2.model';
import { CanvasEdge } from '../../models/edge-v2.model';

/** Removing the Transformation step is an unsaved canvas edit: the rules saved with the workflow are deleted by
 *  the save that drops the step (server-side, inside that save's transaction), never here — so leaving without
 *  saving keeps them. Only the user's unsaved DRAFT rules go with the edit, and only when no other destination of
 *  the same type is left that they could belong to. */
describe('Canvas — removing the Transformation step leaves saved rules to the save', () => {
  const WORKFLOW_ID = 'wf-1';
  let rules: jasmine.SpyObj<TransformationRulesService>;
  let store: PipelineStoreV2;

  const node = (id: string, transformId: string | null, fields: Record<string, string> = {}): CanvasNode =>
    (transformId
      ? { id, kind: 'transform', transformId, x: 0, y: 0, fields: { __name: id, ...fields } }
      : { id, x: 0, y: 0, fields: { __name: id, ...fields } }) as unknown as CanvasNode;

  /** src → map → tra → sql, plus optional extra nodes. */
  function setUp(workflowId: string | null, extraNodes: CanvasNode[] = []) {
    rules = jasmine.createSpyObj('TransformationRulesService', ['deleteForDestination', 'deletePending']);
    rules.deleteForDestination.and.returnValue(of(1));
    rules.deletePending.and.returnValue(of(0));
    TestBed.configureTestingModule({
      providers: [
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show']) },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: TransformationRulesService, useValue: rules },
      ],
    });
    TestBed.overrideComponent(CanvasComponent, { set: { template: '', imports: [] } });
    store = TestBed.inject(PipelineStoreV2);
    store.loadGraph([
      node('src', null),
      node('map', 'field-mapping'),
      node('tra', 'transformation'),
      node('sql', 'dest-sqlserver', { destinationId: 'dest-config-1' }),
      ...extraNodes,
    ], [
      { id: 'e1', from: 'src', to: 'map' }, { id: 'e2', from: 'map', to: 'tra' }, { id: 'e3', from: 'tra', to: 'sql' },
    ] as CanvasEdge[]);
    const fixture = TestBed.createComponent(CanvasComponent);
    fixture.componentRef.setInput('workflowId', workflowId);
    return fixture.componentInstance as unknown as { onNodeDelete(id: string): void; confirmDelete(): void };
  }

  function remove(canvas: { onNodeDelete(id: string): void; confirmDelete(): void }, id: string) {
    canvas.onNodeDelete(id);
    canvas.confirmDelete();
  }

  it('removes the step at once, deletes no saved rule, and discards only the unsaved drafts', () => {
    const canvas = setUp(WORKFLOW_ID);

    remove(canvas, 'tra');

    expect(store.byId('tra')).toBeUndefined();
    expect(store.hasEdge('map', 'sql')).toBeTrue();
    expect(rules.deleteForDestination).not.toHaveBeenCalled();
    expect(rules.deletePending).toHaveBeenCalledOnceWith(['SqlServer'] as never);
  });

  it('on a workflow that was never saved, discards the drafts the same way', () => {
    const canvas = setUp(null);

    remove(canvas, 'tra');

    expect(rules.deleteForDestination).not.toHaveBeenCalled();
    expect(rules.deletePending).toHaveBeenCalledOnceWith(['SqlServer'] as never);
  });

  it('keeps the drafts when another destination of the same type remains', () => {
    const canvas = setUp(WORKFLOW_ID, [node('sql2', 'dest-sqlserver', { destinationId: 'dest-config-2' })]);

    remove(canvas, 'tra');

    expect(rules.deletePending).not.toHaveBeenCalled();
    expect(rules.deleteForDestination).not.toHaveBeenCalled();
  });

  it('treats a Mapping delete that takes the step with it the same way', () => {
    const canvas = setUp(WORKFLOW_ID);

    remove(canvas, 'map');

    expect(store.byId('tra')).toBeUndefined();
    expect(rules.deleteForDestination).not.toHaveBeenCalled();
    expect(rules.deletePending).toHaveBeenCalledOnceWith(['SqlServer'] as never);
  });

  it('touches no rule when no Transformation step is removed', () => {
    // A second, unconnected source: deleting it removes no Transformation step.
    const canvas = setUp(WORKFLOW_ID, [node('src2', null)]);

    remove(canvas, 'src2');

    expect(rules.deleteForDestination).not.toHaveBeenCalled();
    expect(rules.deletePending).not.toHaveBeenCalled();
  });
});
