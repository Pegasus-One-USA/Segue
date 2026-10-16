import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { EMPTY, Subject, of } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { DashboardService } from '../../services/dashboard.service';
import { PipelineRunService } from '../../services/pipeline-run.service';
import { ExecutionHistoryApiService } from '../../../execution-history/services/execution-history-api.service';
import { RunStatusChangedEvent, RunStatusHubService } from '../../../services/run-status-hub.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { WorkflowApiService } from '../../../services/workflow-api.service';

/** A run the Worker finishes now reaches the Dashboard through the API's Redis relay; with several API replicas the
 *  same event can arrive more than once. Each burst must cost one silent refetch, not one per copy. */
describe('Dashboard live run status', () => {
  let events$: Subject<RunStatusChangedEvent>;
  let fetchRecent: jasmine.Spy;
  let statusCounts: jasmine.Spy;

  const succeeded: RunStatusChangedEvent = {
    workflowRunId: 'run-1', workflowDefinitionId: 'wf-bulk', status: 'Succeeded',
    occurredAt: '2026-10-13T09:19:47Z', errorMessage: null, errorReferenceId: null,
  };

  beforeEach(() => {
    events$ = new Subject<RunStatusChangedEvent>();
    fetchRecent = jasmine.createSpy('fetchRecent');
    statusCounts = jasmine.createSpy('statusCounts').and.returnValue(of({}));
    TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideRouter([]),
        { provide: DashboardService, useValue: { lastRefreshed: signal(new Date()), refresh: () => undefined } },
        { provide: PipelineRunService, useValue: { runs: signal([]), fetchRecent } },
        { provide: ExecutionHistoryApiService, useValue: { statusCounts } },
        {
          provide: RunStatusHubService,
          useValue: { ensureConnected: () => undefined, runStatusChanged$: events$.asObservable(), reconnected$: EMPTY },
        },
        { provide: PermissionService, useValue: { hasPermission: () => true } },
        { provide: WorkflowApiService, useValue: {} },
      ],
    });
  });

  it('refetches once, silently, for a pushed terminal event and its duplicates', fakeAsync(() => {
    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    statusCounts.calls.reset();

    events$.next(succeeded);
    events$.next(succeeded);
    events$.next(succeeded);
    tick(1000);

    expect(fetchRecent).toHaveBeenCalledTimes(1);
    expect(fetchRecent).toHaveBeenCalledWith(true);
    expect(statusCounts).toHaveBeenCalledTimes(1);
    expect(statusCounts).toHaveBeenCalledWith({ silent: true });
    fixture.destroy();
  }));
});
