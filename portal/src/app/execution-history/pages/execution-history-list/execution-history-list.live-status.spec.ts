import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { ExecutionHistoryListComponent } from './execution-history-list.component';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { RunStatusChangedEvent, RunStatusHubService } from '../../../services/run-status-hub.service';
import { PhaseConfigService } from '../../../services/phase-config.service';
import { PermissionService } from '../../../auth/services/permission.service';

/** Bug: a run's row stays "Running" on this page until a manual refresh, while the Workflows list (which listens
 *  to RunStatusHubService) flips to Succeeded live. These tests pin the page's reaction to a pushed status event. */
describe('ExecutionHistoryListComponent live run status', () => {
  const workflowId = 'wf-1';
  let events$: Subject<RunStatusChangedEvent>;
  let reconnected$: Subject<void>;
  let api: jasmine.SpyObj<ExecutionHistoryApiService>;

  const page = (status: string) => ({
    items: [{ id: 'run-1', pipelineName: 'WF', status }],
    totalCount: 1, page: 1, pageSize: 10,
    availableStatuses: [], availableApplicationTypes: [],
  });

  beforeEach(() => {
    events$ = new Subject<RunStatusChangedEvent>();
    reconnected$ = new Subject<void>();
    api = jasmine.createSpyObj<ExecutionHistoryApiService>('ExecutionHistoryApiService', ['list']);
    api.list.and.returnValue(of(page('Running')) as never);

    TestBed.configureTestingModule({
      imports: [ExecutionHistoryListComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ workflowId }) } } },
        { provide: ExecutionHistoryApiService, useValue: api },
        {
          provide: RunStatusHubService,
          useValue: {
            ensureConnected: jasmine.createSpy('ensureConnected'),
            runStatusChanged$: events$.asObservable(),
            reconnected$: reconnected$.asObservable(),
          },
        },
        { provide: PhaseConfigService, useValue: { isSourceEnabled: () => true, isTransformEnabled: () => true } },
        { provide: PermissionService, useValue: { hasPermission: () => true } },
      ],
    });
  });

  it('re-fetches when a run of this workflow reaches a terminal status', fakeAsync(() => {
    const fixture = TestBed.createComponent(ExecutionHistoryListComponent);
    fixture.detectChanges();
    expect(api.list).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.result().items[0].status).toBe('Running');

    api.list.and.returnValue(of(page('Succeeded')) as never);
    events$.next({
      workflowRunId: 'run-1', workflowDefinitionId: workflowId, status: 'Succeeded',
      occurredAt: new Date().toISOString(), errorMessage: null, errorReferenceId: null,
    });
    tick(1000);

    expect(api.list).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.result().items[0].status).toBe('Succeeded');
    fixture.destroy();
  }));

  const eventFor = (definitionId: string, runId = 'run-1'): RunStatusChangedEvent => ({
    workflowRunId: runId, workflowDefinitionId: definitionId, status: 'Succeeded',
    occurredAt: new Date().toISOString(), errorMessage: null, errorReferenceId: null,
  });

  it('ignores events for other workflows while drilled into one', fakeAsync(() => {
    const fixture = TestBed.createComponent(ExecutionHistoryListComponent);
    fixture.detectChanges();
    events$.next(eventFor('some-other-workflow'));
    tick(1000);
    expect(api.list).toHaveBeenCalledTimes(1);
    fixture.destroy();
  }));

  it('collapses a burst of events into one background re-fetch, without the search spinner or global loader', fakeAsync(() => {
    const fixture = TestBed.createComponent(ExecutionHistoryListComponent);
    fixture.detectChanges();
    events$.next(eventFor(workflowId, 'run-1'));
    events$.next(eventFor(workflowId, 'run-2'));
    events$.next(eventFor(workflowId, 'run-3'));
    expect(fixture.componentInstance.searching()).toBeFalse();
    tick(1000);
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(api.list.calls.mostRecent().args[1]).toEqual({ silent: true });
    expect(fixture.componentInstance.searching()).toBeFalse();
    fixture.destroy();
  }));

  it('re-fetches after the hub reconnects, to catch events missed while disconnected', fakeAsync(() => {
    const fixture = TestBed.createComponent(ExecutionHistoryListComponent);
    fixture.detectChanges();
    reconnected$.next();
    tick(1000);
    expect(api.list).toHaveBeenCalledTimes(2);
    fixture.destroy();
  }));
});
