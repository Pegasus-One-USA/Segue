import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Subject, of } from 'rxjs';
import { WorkflowBuilderV2Component } from './workflow-builder-v2.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { WorkflowApiService, WorkflowDefinitionDto } from '../../services/workflow-api.service';
import { WorkflowGraphMapperServiceV2 } from '../../services/workflow-graph-mapper-v2.service';
import { WorkflowBuildAssemblerServiceV2 } from '../../services/workflow-build-assembler-v2.service';
import { TransformationRulesService } from '../../components/node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { PermissionService } from '../../auth/services/permission.service';
import { DialogService } from '../../core/services/dialog.service';
import { CanvasNode } from '../../models/node-v2.model';

/**
 * Save in the workflow builder asks once ("Write into <EHR> on every run?") when it would add live EHR writing the
 * last saved version did not have: a new live write-back, a test or dry run switched to live, or a live write-back
 * on another connection. Cancel saves nothing; a re-save with no live change, a test run or a dry run does not ask.
 */
describe('WorkflowBuilderV2Component — Save asks before adding a live EHR write', () => {
  let component: WorkflowBuilderV2Component;
  let store: PipelineStoreV2;
  let api: { catalog: () => unknown[]; validate: jasmine.Spy; save: jasmine.Spy; build: jasmine.Spy; load: jasmine.Spy };
  let dialogs: { open: jasmine.Spy };
  let closed: Subject<boolean | undefined>;

  const source = { id: 'n-src', x: 0, y: 0, connected: true, fields: { __name: 'Epic' } } as CanvasNode;
  const writeBack = (fields: Record<string, string>): CanvasNode =>
    ({ id: 'n-ewb', kind: 'transform', transformId: 'dest-ehr-writeback', x: 300, y: 0, fields: { __name: 'EHR Write-Back', ...fields } }) as CanvasNode;
  const live = (connection = 'c-1') => ({ dest_ehrVendor: 'Epic', dest_dryRun: 'false', dest_sourceConnectionId: connection });
  const testRun = { dest_ehrVendor: 'GenericFhir', dest_dryRun: 'false', dest_testAsVendor: 'Epic', dest_sourceConnectionId: 'c-t' };
  const dryRun = { ...live(), dest_dryRun: 'true' };

  /** Puts a source and `node` on the canvas. */
  const canvas = (node: CanvasNode) => {
    store.reset();
    store.addNode(source);
    store.addNode(node);
    store.addEdge({ id: 'e1', from: source.id, to: node.id });
  };

  /** Opens a saved workflow whose write-back has `fields`, as Edit does. */
  const openSaved = (fields: Record<string, string>) => {
    const definition = {
      id: 'wf-1',
      name: 'Allergies to Epic',
      nodes: [{ id: 'n-ewb', configurationJson: JSON.stringify({ __transformId: 'dest-ehr-writeback', ...fields }) }],
      edges: [],
    } as unknown as WorkflowDefinitionDto;
    api.load.and.returnValue(of(definition));
    component['workflowIdInput'].set('wf-1');
    component.onLoadWorkflow();
    canvas(writeBack(fields));
  };

  beforeEach(() => {
    closed = new Subject<boolean | undefined>();
    dialogs = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => closed.asObservable() }) };
    api = {
      catalog: () => [{ nodeType: 'EhrWriteBackDestinationNode' }],
      validate: jasmine.createSpy('validate').and.returnValue(of({ isValid: true, errors: [] })),
      save: jasmine.createSpy('save').and.returnValue(of({ id: 'wf-1', name: 'Allergies to Epic' })),
      build: jasmine.createSpy('build'),
      load: jasmine.createSpy('load'),
    };

    TestBed.configureTestingModule({
      imports: [WorkflowBuilderV2Component],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: Router, useValue: { navigate: jasmine.createSpy('navigate').and.resolveTo(true) } },
        { provide: WorkflowApiService, useValue: api },
        {
          provide: WorkflowGraphMapperServiceV2,
          useValue: {
            savedDisplayName: () => 'node',
            findLaunchSourceId: () => null,
            loadDefinition: () => undefined,
            toRequest: () => ({ nodes: [], edges: [] }),
          },
        },
        {
          provide: WorkflowBuildAssemblerServiceV2,
          useValue: { assemble: () => ({ sources: [], destinations: [], mappings: [] }), lastUnmappedResources: [] },
        },
        { provide: TransformationRulesService, useValue: { attachPending: () => of(0), list: () => of([]) } },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAll: () => true, hasAny: () => true } },
        { provide: DialogService, useValue: dialogs },
      ],
    });
    TestBed.overrideTemplate(WorkflowBuilderV2Component, '');
    component = TestBed.createComponent(WorkflowBuilderV2Component).componentInstance;
    store = TestBed.inject(PipelineStoreV2);
    component.onWorkflowNameInput('Allergies to Epic');
  });

  it('asks before saving a new live write-back, and saves once Save is chosen', () => {
    canvas(writeBack(live()));

    component.onSave();

    expect(dialogs.open).toHaveBeenCalledTimes(1);
    const data = dialogs.open.calls.mostRecent().args[1].data;
    expect(data.title).toBe('Write into Epic on every run?');
    expect(data.message).toBe(
      'This workflow will write records into Epic every time it runs, including scheduled and triggered runs. This cannot be undone.');
    expect(data.confirmLabel).toBe('Save');
    expect(api.save).not.toHaveBeenCalled();

    closed.next(true);
    expect(api.save).toHaveBeenCalledTimes(1);
  });

  it('Cancel saves nothing and leaves the canvas as it was', () => {
    canvas(writeBack(live()));
    const before = store.nodes();

    component.onSave();
    closed.next(false);

    expect(api.validate).not.toHaveBeenCalled();
    expect(api.save).not.toHaveBeenCalled();
    expect(api.build).not.toHaveBeenCalled();
    expect(store.nodes()).toBe(before);
  });

  it('asks when a test run is switched to live', () => {
    openSaved(testRun);
    store.updateNode('n-ewb', { fields: { __name: 'EHR Write-Back', ...live() } });

    component.onSave();

    expect(dialogs.open).toHaveBeenCalledTimes(1);
    expect(api.save).not.toHaveBeenCalled();
  });

  it('asks when a live write-back is moved to another connection', () => {
    openSaved(live('c-1'));
    store.updateNode('n-ewb', { fields: { __name: 'EHR Write-Back', ...live('c-2') } });

    component.onSave();

    expect(dialogs.open).toHaveBeenCalledTimes(1);
  });

  it('does not ask on a re-save with no live change', () => {
    openSaved(live());

    component.onSave();

    expect(dialogs.open).not.toHaveBeenCalled();
    expect(api.save).toHaveBeenCalledTimes(1);
  });

  it('does not ask for a test run or a dry run', () => {
    canvas(writeBack(testRun));
    component.onSave();
    canvas(writeBack(dryRun));
    component.onSave();

    expect(dialogs.open).not.toHaveBeenCalled();
    expect(api.save).toHaveBeenCalledTimes(2);
  });
});
