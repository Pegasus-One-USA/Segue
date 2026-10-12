import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { CanvasComponent } from './canvas.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { ToastService } from '../../services/toast.service';
import { PermissionService } from '../../auth/services/permission.service';
import { TransformationRulesService } from '../node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import {
  DeIdentificationProfileService,
  WorkflowDeIdentificationRuleDeletionResult,
} from '../../destination-connections/services/deidentification-profile.service';
import { CanvasNode } from '../../models/node-v2.model';
import { CanvasEdge } from '../../models/edge-v2.model';

/** Removing a workflow's last De-identification step deletes the rules of the workflow's own policy on the server
 *  FIRST; the canvas changes only once that succeeded. A failure or refusal leaves the canvas as it was. */
describe('Canvas — removing the De-identification step deletes its rules', () => {
  const WORKFLOW_ID = 'wf-1';
  let server: Subject<WorkflowDeIdentificationRuleDeletionResult>;
  let profiles: jasmine.SpyObj<DeIdentificationProfileService>;
  let transformationRules: jasmine.SpyObj<TransformationRulesService>;
  let toast: jasmine.SpyObj<ToastService>;
  let store: PipelineStoreV2;

  const node = (id: string, transformId: string | null, fields: Record<string, string> = {}): CanvasNode =>
    (transformId
      ? { id, kind: 'transform', transformId, x: 0, y: 0, fields: { __name: id, ...fields } }
      : { id, x: 0, y: 0, fields: { __name: id, ...fields } }) as unknown as CanvasNode;

  interface Canvas { onNodeDelete(id: string): void; confirmDelete(): void; cancelDelete(): void; pendingDelete(): unknown }

  /** src → map → deid → sql (+ extra nodes); the destination redacts with the workflow's own policy. */
  function setUp(workflowId: string | null, extraNodes: CanvasNode[] = []): Canvas {
    server = new Subject();
    profiles = jasmine.createSpyObj('DeIdentificationProfileService', ['deleteWorkflowRules']);
    profiles.deleteWorkflowRules.and.returnValue(server);
    transformationRules = jasmine.createSpyObj('TransformationRulesService', ['deleteForDestination', 'deletePending']);
    transformationRules.deleteForDestination.and.returnValue(of(0));
    transformationRules.deletePending.and.returnValue(of(0));
    toast = jasmine.createSpyObj('ToastService', ['show']);
    TestBed.configureTestingModule({
      providers: [
        { provide: ToastService, useValue: toast },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: TransformationRulesService, useValue: transformationRules },
        { provide: DeIdentificationProfileService, useValue: profiles },
      ],
    });
    TestBed.overrideComponent(CanvasComponent, { set: { template: '', imports: [] } });
    store = TestBed.inject(PipelineStoreV2);
    store.loadGraph([
      node('src', null),
      node('map', 'field-mapping'),
      node('deid', 'deidentification'),
      node('sql', 'dest-sqlserver', { destinationId: 'dest-1', deIdentificationProfileId: 'policy-of-wf-1' }),
      ...extraNodes,
    ], [
      { id: 'e1', from: 'src', to: 'map' }, { id: 'e2', from: 'map', to: 'deid' }, { id: 'e3', from: 'deid', to: 'sql' },
    ] as CanvasEdge[]);
    const fixture = TestBed.createComponent(CanvasComponent);
    fixture.componentRef.setInput('workflowId', workflowId);
    return fixture.componentInstance as unknown as Canvas;
  }

  it('deletes the workflow\'s rules first, and only then removes the step and its policy selection', () => {
    const canvas = setUp(WORKFLOW_ID);

    canvas.onNodeDelete('deid');
    canvas.confirmDelete();

    expect(profiles.deleteWorkflowRules).toHaveBeenCalledOnceWith(WORKFLOW_ID);
    expect(store.byId('deid')).withContext('nothing changes before the server answers').toBeDefined();
    expect(store.byId('sql')!.fields['deIdentificationProfileId']).toBe('policy-of-wf-1');

    server.next({ deleted: 3, usedElsewhere: [] });

    expect(store.byId('deid')).toBeUndefined();
    expect(store.byId('sql')!.fields['deIdentificationProfileId']).toBeUndefined();
    expect(store.hasEdge('map', 'sql')).toBeTrue();
  });

  it('leaves the canvas exactly as it was when the server delete fails', () => {
    const canvas = setUp(WORKFLOW_ID);
    canvas.onNodeDelete('deid');
    canvas.confirmDelete();

    server.error({ status: 500 });

    expect(store.byId('deid')).toBeDefined();
    expect(store.byId('sql')!.fields['deIdentificationProfileId']).toBe('policy-of-wf-1');
    expect(toast.show).toHaveBeenCalledWith('Nothing was deleted', jasmine.stringContaining('could not be deleted'));
  });

  it('leaves the canvas as it was, and says why, when the policy is used elsewhere (409)', () => {
    const canvas = setUp(WORKFLOW_ID);
    canvas.onNodeDelete('deid');
    canvas.confirmDelete();

    server.error({ status: 409, error: { deleted: 0, usedElsewhere: ["workflow 'Other pipeline'"] } });

    expect(store.byId('deid')).toBeDefined();
    expect(toast.show).toHaveBeenCalledWith('Nothing was deleted', jasmine.stringContaining("workflow 'Other pipeline'"));
  });

  it('ignores a second confirm and a cancel while the server is deleting', () => {
    const canvas = setUp(WORKFLOW_ID);
    canvas.onNodeDelete('deid');
    canvas.confirmDelete();
    canvas.confirmDelete();
    canvas.cancelDelete();

    expect(profiles.deleteWorkflowRules).toHaveBeenCalledTimes(1);
    expect(canvas.pendingDelete()).not.toBeNull();
  });

  it('deletes them when a Mapping delete takes the step with it', () => {
    const canvas = setUp(WORKFLOW_ID);
    canvas.onNodeDelete('map');
    canvas.confirmDelete();
    server.next({ deleted: 1, usedElsewhere: [] });

    expect(profiles.deleteWorkflowRules).toHaveBeenCalledOnceWith(WORKFLOW_ID);
    expect(store.byId('deid')).toBeUndefined();
  });

  it('keeps them while another De-identification step in the workflow remains', () => {
    const canvas = setUp(WORKFLOW_ID, [node('deid2', 'deidentification')]);
    canvas.onNodeDelete('deid');
    canvas.confirmDelete();

    expect(profiles.deleteWorkflowRules).not.toHaveBeenCalled();
    expect(store.byId('deid')).toBeUndefined();
  });

  it('on a workflow that was never saved there is no policy, so nothing is asked of the server', () => {
    const canvas = setUp(null);
    canvas.onNodeDelete('deid');
    canvas.confirmDelete();

    expect(profiles.deleteWorkflowRules).not.toHaveBeenCalled();
    expect(store.byId('deid')).toBeUndefined();
  });

  it('removing another module type never deletes de-identification rules', () => {
    const canvas = setUp(WORKFLOW_ID, [node('src2', null)]);
    canvas.onNodeDelete('src2');
    canvas.confirmDelete();

    expect(profiles.deleteWorkflowRules).not.toHaveBeenCalled();
  });
});
