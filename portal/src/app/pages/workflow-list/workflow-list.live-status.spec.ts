import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EMPTY, Subject } from 'rxjs';
import { WorkflowListComponent } from './workflow-list.component';
import { WorkflowApiService, WorkflowSummary } from '../../services/workflow-api.service';
import { ToastService } from '../../services/toast.service';
import { RunStatusChangedEvent, RunStatusHubService } from '../../services/run-status-hub.service';
import { PermissionService } from '../../auth/services/permission.service';
import { AuthStore } from '../../auth/store/auth.store';
import { PhaseConfigService } from '../../services/phase-config.service';

/** A run the Worker finishes (resumed after a bulk export, or scheduled) now reaches the browser through the API's
 *  Redis relay — with several API replicas, the same event can arrive more than once. The row must end on the pushed
 *  status, and a repeat of the same event must leave it exactly as the first one did. */
describe('WorkflowListComponent live run status', () => {
  const workflowId = 'wf-bulk';
  let events$: Subject<RunStatusChangedEvent>;

  const succeeded = (occurredAt: string): RunStatusChangedEvent => ({
    workflowRunId: 'run-1', workflowDefinitionId: workflowId, status: 'Succeeded',
    occurredAt, errorMessage: null, errorReferenceId: null,
  });

  function mount() {
    events$ = new Subject<RunStatusChangedEvent>();
    TestBed.configureTestingModule({
      imports: [WorkflowListComponent],
      providers: [
        provideRouter([]),
        { provide: WorkflowApiService, useValue: {} },
        { provide: ToastService, useValue: { success: () => undefined, error: () => undefined } },
        {
          provide: RunStatusHubService,
          useValue: { ensureConnected: () => undefined, runStatusChanged$: events$.asObservable(), reconnected$: EMPTY },
        },
        { provide: PermissionService, useValue: { hasPermission: () => true } },
        { provide: AuthStore, useValue: { hasRole: () => false } },
        { provide: PhaseConfigService, useValue: { isSourceEnabled: () => true, isTransformEnabled: () => true } },
      ],
    });
    // Not detectChanges(): ngOnInit's REST load is not what is under test, only the constructor's hub subscription.
    const component = TestBed.createComponent(WorkflowListComponent).componentInstance;
    component.summaries.set([
      { workflowId, name: 'ECW BACKEND BULK EXPORT', lastRun: 'Running', lastRunAt: '2026-10-13T09:17:04Z' } as unknown as WorkflowSummary,
      { workflowId: 'wf-other', name: 'Other', lastRun: 'Running', lastRunAt: '2026-10-13T09:00:00Z' } as unknown as WorkflowSummary,
    ]);
    return component;
  }

  it('flips the row to Succeeded when the Worker-finished run is pushed', () => {
    const component = mount();

    events$.next(succeeded('2026-10-13T09:19:47Z'));

    const row = component.summaries().find(w => w.workflowId === workflowId)!;
    expect(row.lastRun).toBe('Succeeded');
    expect(row.lastRunAt).toBe('2026-10-13T09:19:47Z');
    expect(component.summaries().find(w => w.workflowId === 'wf-other')!.lastRun).toBe('Running');
  });

  it('leaves the row unchanged when the same event arrives again', () => {
    const component = mount();

    events$.next(succeeded('2026-10-13T09:19:47Z'));
    const afterFirst = component.summaries();
    events$.next(succeeded('2026-10-13T09:19:47Z'));

    expect(component.summaries()).toEqual(afterFirst);
  });
});
