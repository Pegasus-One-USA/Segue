import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { CanvasComponent } from './canvas.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { ToastService } from '../../services/toast.service';
import { PermissionService } from '../../auth/services/permission.service';
import { TransformationRulesService } from '../node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { CanvasNode } from '../../models/node-v2.model';
import { CanvasEdge } from '../../models/edge-v2.model';

/** Removing the De-identification step is an unsaved canvas edit and deletes NOTHING on the server: the
 *  workflow's de-identification rules go only with the save that drops the step (server-side, inside that save's
 *  transaction). So leaving without saving, putting the step back, or a failed save can never leave a saved
 *  workflow whose De-identification step points at an emptied policy. */
describe('Canvas — removing the De-identification step leaves its rules to the save', () => {
  let http: HttpTestingController;
  let transformationRules: jasmine.SpyObj<TransformationRulesService>;
  let store: PipelineStoreV2;

  const node = (id: string, transformId: string | null, fields: Record<string, string> = {}): CanvasNode =>
    (transformId
      ? { id, kind: 'transform', transformId, x: 0, y: 0, fields: { __name: id, ...fields } }
      : { id, x: 0, y: 0, fields: { __name: id, ...fields } }) as unknown as CanvasNode;

  interface Canvas { onNodeDelete(id: string): void; confirmDelete(): void; pendingDelete(): { serverSideLeftovers: string[] } | null }

  /** src → map → deid → sql (+ extra nodes); the destination redacts with the workflow's own policy. */
  function setUp(extraNodes: CanvasNode[] = []): Canvas {
    transformationRules = jasmine.createSpyObj('TransformationRulesService', ['deleteForDestination', 'deletePending']);
    transformationRules.deleteForDestination.and.returnValue(of(0));
    transformationRules.deletePending.and.returnValue(of(0));
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show']) },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: TransformationRulesService, useValue: transformationRules },
      ],
    });
    TestBed.overrideComponent(CanvasComponent, { set: { template: '', imports: [] } });
    store = TestBed.inject(PipelineStoreV2);
    http = TestBed.inject(HttpTestingController);
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
    fixture.componentRef.setInput('workflowId', 'wf-1');
    return fixture.componentInstance as unknown as Canvas;
  }

  afterEach(() => http.verify());   // fails the test if ANY request was made

  it('removes the step and its policy selection at once, and sends nothing to the server', () => {
    const canvas = setUp();
    canvas.onNodeDelete('deid');
    expect(canvas.pendingDelete()!.serverSideLeftovers.join(' ')).toContain('deleted when you save the workflow');

    canvas.confirmDelete();

    expect(store.byId('deid')).toBeUndefined();
    expect(store.byId('sql')!.fields['deIdentificationProfileId']).toBeUndefined();
    expect(store.hasEdge('map', 'sql')).toBeTrue();
    expect(transformationRules.deleteForDestination).not.toHaveBeenCalled();
    expect(transformationRules.deletePending).not.toHaveBeenCalled();
  });

  it('sends nothing either when a Mapping delete takes the step with it', () => {
    const canvas = setUp();
    canvas.onNodeDelete('map');
    canvas.confirmDelete();

    expect(store.byId('deid')).toBeUndefined();
  });

  it('says the rules are kept while another De-identification step in the workflow remains', () => {
    const canvas = setUp([node('deid2', 'deidentification')]);
    canvas.onNodeDelete('deid');

    expect(canvas.pendingDelete()!.serverSideLeftovers.join(' ')).toContain('are kept');
    canvas.confirmDelete();
    expect(store.byId('deid')).toBeUndefined();
  });
});
