import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { CanvasComponent } from './canvas.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { ToastService } from '../../services/toast.service';
import { PermissionService } from '../../auth/services/permission.service';
import { TransformationRulesService } from '../node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { DeIdentificationProfileService } from '../../destination-connections/services/deidentification-profile.service';
import { CanvasNode } from '../../models/node-v2.model';
import { CanvasEdge } from '../../models/edge-v2.model';

/** Removing the Transformation step means "no transformations on this destination": the rules it stood for are
 *  deleted on the server with it. They used to stay — still applied on every run, and putting the step straight
 *  back the next time the destination was saved. */
describe('Canvas — removing the Transformation step deletes its rules', () => {
  const WORKFLOW_ID = 'wf-1';
  const DESTINATION_ID = 'dest-config-1';
  let rules: jasmine.SpyObj<TransformationRulesService>;
  let toast: jasmine.SpyObj<ToastService>;
  let store: PipelineStoreV2;

  const node = (id: string, transformId: string | null, fields: Record<string, string> = {}): CanvasNode =>
    (transformId
      ? { id, kind: 'transform', transformId, x: 0, y: 0, fields: { __name: id, ...fields } }
      : { id, x: 0, y: 0, fields: { __name: id, ...fields } }) as unknown as CanvasNode;

  /** src → map → tra → sql, plus optional extra nodes. */
  function setUp(workflowId: string | null, extraNodes: CanvasNode[] = [], extraEdges: CanvasEdge[] = []) {
    rules = jasmine.createSpyObj('TransformationRulesService', ['deleteForDestination', 'deletePending']);
    rules.deleteForDestination.and.returnValue(of(1));
    rules.deletePending.and.returnValue(of(0));
    toast = jasmine.createSpyObj('ToastService', ['show', 'error', 'success', 'warning']);
    TestBed.configureTestingModule({
      providers: [
        { provide: ToastService, useValue: toast },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: TransformationRulesService, useValue: rules },
        { provide: DeIdentificationProfileService, useValue: jasmine.createSpyObj('DeIdentificationProfileService', ['deleteWorkflowRules']) },
      ],
    });
    TestBed.overrideComponent(CanvasComponent, { set: { template: '', imports: [] } });
    store = TestBed.inject(PipelineStoreV2);
    store.loadGraph([
      node('src', null),
      node('map', 'field-mapping'),
      node('tra', 'transformation'),
      node('sql', 'dest-sqlserver', { destinationId: DESTINATION_ID }),
      ...extraNodes,
    ], [
      { id: 'e1', from: 'src', to: 'map' }, { id: 'e2', from: 'map', to: 'tra' }, { id: 'e3', from: 'tra', to: 'sql' },
      ...extraEdges,
    ] as CanvasEdge[]);
    const fixture = TestBed.createComponent(CanvasComponent);
    fixture.componentRef.setInput('workflowId', workflowId);
    return fixture.componentInstance as unknown as { onNodeDelete(id: string): void; confirmDelete(): void };
  }

  function remove(canvas: { onNodeDelete(id: string): void; confirmDelete(): void }, id: string) {
    canvas.onNodeDelete(id);
    canvas.confirmDelete();
  }

  it('deletes the saved rules for that destination, and its unsaved drafts, when the destination stays', () => {
    const canvas = setUp(WORKFLOW_ID);

    remove(canvas, 'tra');

    expect(store.byId('tra')).toBeUndefined();
    expect(store.byId('sql')).toBeDefined();
    expect(rules.deleteForDestination).toHaveBeenCalledOnceWith({
      workflowId: WORKFLOW_ID,
      destinationConfigurationId: DESTINATION_ID,
      destinationType: 'SqlServer',
      otherDestinationsOfThisTypeRemain: false,
    } as never);
    expect(rules.deletePending).toHaveBeenCalledOnceWith(['SqlServer'] as never);
  });

  it('on a workflow that was never saved, deletes the drafts only', () => {
    const canvas = setUp(null);

    remove(canvas, 'tra');

    expect(rules.deleteForDestination).not.toHaveBeenCalled();
    expect(rules.deletePending).toHaveBeenCalledOnceWith(['SqlServer'] as never);
  });

  it('keeps drafts and unattributed rules when another destination of the same type remains', () => {
    const canvas = setUp(WORKFLOW_ID, [node('sql2', 'dest-sqlserver', { destinationId: 'dest-config-2' })]);

    remove(canvas, 'tra');

    expect(rules.deleteForDestination).toHaveBeenCalledOnceWith(jasmine.objectContaining({
      destinationConfigurationId: DESTINATION_ID,
      otherDestinationsOfThisTypeRemain: true,
    }) as never);
    expect(rules.deletePending).not.toHaveBeenCalled();
  });

  it('deletes them when a Mapping delete takes the Transformation step with it', () => {
    const canvas = setUp(WORKFLOW_ID);

    remove(canvas, 'map');

    expect(store.byId('tra')).toBeUndefined();
    expect(rules.deleteForDestination).toHaveBeenCalledTimes(1);
  });

  it('makes no rule delete when no Transformation step is removed', () => {
    // A second, unconnected source: deleting it removes no Transformation step.
    const canvas = setUp(WORKFLOW_ID, [node('src2', null)]);

    remove(canvas, 'src2');

    expect(rules.deleteForDestination).not.toHaveBeenCalled();
    expect(rules.deletePending).not.toHaveBeenCalled();
  });

  it('says so when the server delete fails, instead of failing silently', () => {
    const canvas = setUp(WORKFLOW_ID);
    rules.deleteForDestination.and.returnValue(throwError(() => new Error('500')));

    remove(canvas, 'tra');

    expect(toast.show).toHaveBeenCalledWith('Transformation rules not deleted', jasmine.stringContaining('could not be deleted'));
  });
});
