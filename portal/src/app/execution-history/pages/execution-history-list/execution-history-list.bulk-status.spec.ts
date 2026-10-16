import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { ExecutionHistoryListComponent } from './execution-history-list.component';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { BulkExportStatus, RouteExecution } from '../../models/execution-history.model';
import { RunStatusChangedEvent, RunStatusHubService } from '../../../services/run-status-hub.service';
import { PhaseConfigService } from '../../../services/phase-config.service';
import { PermissionService } from '../../../auth/services/permission.service';

/** Bug: the Bulk Data Status Request popup was a read taken when it opened. Left open while the export finished, it
 *  kept "In Progress" with every resource type "Pending" for good, while the list behind it already said Succeeded.
 *  It now re-reads when the run it shows changes status (and only that run). */
describe('ExecutionHistoryListComponent Bulk Data Status popup', () => {
  let events$: Subject<RunStatusChangedEvent>;
  let reconnected$: Subject<void>;
  let api: jasmine.SpyObj<ExecutionHistoryApiService>;

  const row = (id: string): RouteExecution => ({
    id, workflowDefinitionId: 'wf-bulk', pipelineName: 'ECW BACKEND BULK EXPORT', status: 'AwaitingBulkExport',
    bulkRequestId: '$export-poll-location',
  } as unknown as RouteExecution);

  const inProgress: BulkExportStatus = {
    bulkRequestId: '$export-poll-location', status: 'InProgress', progress: '0% Completed',
    kickedOffOnUtc: '2026-10-13T09:17:16Z', nextPollNotBeforeUtc: '2026-10-13T09:17:52Z', pollAttemptCount: 1,
    transactionTime: null, request: null, requiresAccessToken: null,
    resourceTypes: [{ resourceType: 'Patient', fileCount: null, state: 'Pending' }],
    errors: [], retryAfterSeconds: null, errorMessage: null,
  };
  const completed: BulkExportStatus = {
    ...inProgress, status: 'Completed', progress: null, nextPollNotBeforeUtc: null,
    transactionTime: '2026-10-13T09:19:03Z',
    request: 'http://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD/Group/5a762949/$export',
    resourceTypes: [{ resourceType: 'Patient', fileCount: 1, state: 'Ready' }],
  };
  const failed: BulkExportStatus = {
    ...inProgress, status: 'Failed', progress: null, nextPollNotBeforeUtc: null,
    resourceTypes: [{ resourceType: 'Patient', fileCount: null, state: 'Failed' }],
    errorMessage: 'Bulk export did not complete after 120 status polls.',
  };

  const event = (runId: string, status: RunStatusChangedEvent['status'] = 'Succeeded'): RunStatusChangedEvent => ({
    workflowRunId: runId, workflowDefinitionId: 'wf-bulk', status,
    occurredAt: new Date().toISOString(), errorMessage: null, errorReferenceId: null,
  });

  beforeEach(() => {
    events$ = new Subject<RunStatusChangedEvent>();
    reconnected$ = new Subject<void>();
    api = jasmine.createSpyObj<ExecutionHistoryApiService>('ExecutionHistoryApiService', ['list', 'bulkExportStatus']);
    api.list.and.returnValue(of({
      items: [row('run-a'), row('run-b')], totalCount: 2, page: 1, pageSize: 10,
      availableStatuses: [], availableApplicationTypes: [],
    }) as never);

    TestBed.configureTestingModule({
      imports: [ExecutionHistoryListComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
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

  function mount() {
    const fixture = TestBed.createComponent(ExecutionHistoryListComponent);
    fixture.detectChanges();
    return { fixture, component: fixture.componentInstance };
  }

  const open = (component: ExecutionHistoryListComponent, runId: string) =>
    component.openBulkStatus(row(runId), new MouseEvent('click'));

  const popupText = () => (document.querySelector('.bulk-status') as HTMLElement | null)?.textContent ?? '';

  it('shows the completed export and its ready resource types once the run finishes, without reopening', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');
    fixture.detectChanges();
    expect(popupText()).toContain('In Progress');
    expect(popupText()).toContain('Pending');

    // The Worker finishes the run: its status event reaches the browser through the API relay.
    api.bulkExportStatus.and.returnValue(of(completed));
    events$.next(event('run-a'));
    tick(1000);
    fixture.detectChanges();

    expect(api.bulkExportStatus).toHaveBeenCalledTimes(2);
    expect(api.bulkExportStatus.calls.mostRecent().args).toEqual(['run-a', { silent: true }]);
    expect(component.bulkStatusOpen()).toBeTrue();
    expect(component.bulkStatus()!.status).toBe('Completed');
    expect(component.bulkStatus()!.resourceTypes).toEqual([{ resourceType: 'Patient', fileCount: 1, state: 'Ready' }]);
    expect(popupText()).toContain('Completed');
    expect(popupText()).toContain('Ready');
    expect(popupText()).not.toContain('In Progress');
    // The list behind it refreshed as before (the Execution History fix).
    expect(api.list.calls.count()).toBeGreaterThan(1);
    fixture.destroy();
  }));

  it('shows a failed export and marks its resource types failed', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');

    api.bulkExportStatus.and.returnValue(of(failed));
    events$.next(event('run-a', 'Failed'));
    tick(1000);
    fixture.detectChanges();

    expect(component.bulkStatus()!.status).toBe('Failed');
    expect(component.bulkStatus()!.resourceTypes[0].state).toBe('Failed');
    expect(popupText()).toContain('Bulk export did not complete after 120 status polls.');
    fixture.destroy();
  }));

  it('ignores status changes of other runs, such as a second bulk export running at the same time', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');

    events$.next(event('run-b'));
    tick(1000);

    expect(api.bulkExportStatus).toHaveBeenCalledTimes(1);
    expect(component.bulkStatus()!.status).toBe('InProgress');
    fixture.destroy();
  }));

  it('re-reads once for a burst of duplicate events', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');

    api.bulkExportStatus.and.returnValue(of(completed));
    events$.next(event('run-a'));
    events$.next(event('run-a'));
    events$.next(event('run-a'));
    tick(1000);

    expect(api.bulkExportStatus).toHaveBeenCalledTimes(2);
    expect(component.bulkStatus()!.status).toBe('Completed');
    fixture.destroy();
  }));

  it('never shows an older read that answers after the popup moved to another run', fakeAsync(() => {
    const { fixture, component } = mount();
    const runA$ = new Subject<BulkExportStatus | null>();
    api.bulkExportStatus.and.callFake((id: string) =>
      (id === 'run-a' ? runA$.asObservable() : of(completed)) as Observable<BulkExportStatus | null>);

    open(component, 'run-a');           // run A's read is slow to answer
    component.closeBulkStatus();
    open(component, 'run-b');           // reopened on run B, which answers at once
    runA$.next(inProgress);             // run A's stale answer arrives last
    fixture.detectChanges();

    expect(component.bulkStatusRun()!.id).toBe('run-b');
    expect(component.bulkStatus()).toEqual(completed);
    expect(runA$.observed).toBeFalse(); // the stale request was cancelled, not just ignored
    fixture.destroy();
  }));

  it('does nothing for the run once the popup is closed', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');
    component.closeBulkStatus();

    events$.next(event('run-a'));
    reconnected$.next();
    tick(1000);

    expect(api.bulkExportStatus).toHaveBeenCalledTimes(1);
    expect(component.bulkStatus()).toBeNull();
    fixture.destroy();
  }));

  it('reopening reads the current status afresh', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');
    component.closeBulkStatus();

    api.bulkExportStatus.and.returnValue(of(completed));
    open(component, 'run-a');

    expect(api.bulkExportStatus.calls.mostRecent().args).toEqual(['run-a', { silent: false }]);
    expect(component.bulkStatus()!.status).toBe('Completed');
    fixture.destroy();
  }));

  it('re-reads after the live connection reconnects, catching a finish it missed', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');

    api.bulkExportStatus.and.returnValue(of(completed));
    reconnected$.next();

    expect(api.bulkExportStatus).toHaveBeenCalledTimes(2);
    expect(component.bulkStatus()!.status).toBe('Completed');
    fixture.destroy();
  }));

  // ── Elapsed ──────────────────────────────────────────────────────────────────────────────────────────────
  // Bug: Elapsed was always kick-off-to-now, so it kept growing after the export completed (3m 51s against a
  // 5:02:51 start and a 5:04:03 transaction time). Running: kick-off to now. Ended: kick-off to the end.

  const elapsedText = () => {
    const facts = Array.from(document.querySelectorAll('.bulk-status__facts > div'));
    const elapsed = facts.find(div => div.querySelector('dt')?.textContent?.trim() === 'Export elapsed');
    return elapsed?.querySelector('dd')?.textContent?.trim() ?? '';
  };
  const at = (iso: string) => Date.parse(iso);

  it('counts a running export up to now', () => {
    const { component } = mount();
    expect(component.bulkElapsed(inProgress, at('2026-10-13T09:17:46Z'))).toBe('30s');
    expect(component.bulkElapsed(inProgress, at('2026-10-13T09:19:46Z'))).toBe('2m 30s');
  });

  it('measures a completed export from kick-off to its transaction time, however late it is read', () => {
    const { component } = mount();
    // 09:17:16Z -> 09:19:03Z
    expect(component.bulkElapsed(completed, at('2026-10-13T09:19:10Z'))).toBe('1m 47s');
    expect(component.bulkElapsed(completed, at('2026-10-14T12:00:00Z'))).toBe('1m 47s');
  });

  it('stops failed and cancelled exports at the end FHIRBridge recorded', () => {
    const { component } = mount();
    const ended = { completedOnUtc: '2026-10-13T09:27:16Z' };
    expect(component.bulkElapsed({ ...failed, ...ended }, at('2026-10-14T00:00:00Z'))).toBe('10m 0s');
    expect(component.bulkElapsed({ ...failed, status: 'Cancelled', ...ended }, at('2026-10-14T00:00:00Z'))).toBe('10m 0s');
    expect(component.bulkEndedAt({ ...failed, ...ended })).toBe('2026-10-13T09:27:16Z');
    expect(component.bulkEndedAt(completed)).toBeNull(); // Transaction time is shown instead
  });

  it('reads timestamps as instants whatever their offset', () => {
    const { component } = mount();
    // The same 09:19:03 UTC instant, written in IST.
    expect(component.bulkElapsed({ ...completed, transactionTime: '2026-10-13T14:49:03+05:30' })).toBe('1m 47s');
  });

  it('never shows a negative or unbounded figure for missing, invalid or skewed timestamps', () => {
    const { component } = mount();
    const now = at('2026-10-14T00:00:00Z');
    // Ended, but no end at all: unknown, not "still growing".
    expect(component.bulkElapsed({ ...failed, completedOnUtc: null }, now)).toBe('—');
    expect(component.bulkElapsed({ ...completed, transactionTime: 'not a date', completedOnUtc: undefined }, now)).toBe('—');
    expect(component.bulkElapsed({ ...inProgress, kickedOffOnUtc: 'garbage' }, now)).toBe('—');
    // The source's clock is behind ours: its transactionTime precedes our kick-off, so the recorded end is used.
    expect(component.bulkElapsed(
      { ...completed, transactionTime: '2026-10-13T09:17:00Z', completedOnUtc: '2026-10-13T09:19:34Z' }, now)).toBe('2m 18s');
    expect(component.bulkElapsed({ ...completed, transactionTime: '2026-10-13T09:17:00Z', completedOnUtc: null }, now)).toBe('—');
    // The browser's clock is behind ours while it runs: 0s, not negative.
    expect(component.bulkElapsed(inProgress, at('2026-10-13T09:17:00Z'))).toBe('0s');
    // A stored, non-terminal job status (source no longer readable) is still running.
    expect(component.bulkElapsed({ ...inProgress, status: 'Polling' as never }, at('2026-10-13T09:18:16Z'))).toBe('1m 0s');
  });

  it('freezes Elapsed on screen when the final status arrives while the popup is open', fakeAsync(() => {
    const { fixture, component } = mount();
    const startedJustNow = { ...inProgress, kickedOffOnUtc: new Date(Date.now()).toISOString() };
    api.bulkExportStatus.and.returnValue(of(startedJustNow));
    open(component, 'run-a');
    fixture.detectChanges();
    expect(elapsedText()).toBe('0s');

    tick(90_000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 30s'); // running: grows with the clock

    const kickedOff = Date.parse(startedJustNow.kickedOffOnUtc);
    api.bulkExportStatus.and.returnValue(of({
      ...completed,
      kickedOffOnUtc: startedJustNow.kickedOffOnUtc,
      transactionTime: new Date(kickedOff + 72_000).toISOString(),
    }));
    events$.next(event('run-a'));
    tick(1000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 12s'); // the transaction time, not "now"

    tick(10 * 60_000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 12s'); // and it stays there
    fixture.destroy();
  }));

  // ── One-second tick ──────────────────────────────────────────────────────────────────────────────────────
  // Running: Elapsed is redrawn every second, derived from Started each time. Ended: no interval left at all — a
  // fakeAsync test fails by itself if a periodic timer is still queued when it ends, so every test below that does
  // not call discardPeriodicTasks() also proves the interval was cleared.

  /** Counts intervals started and cleared, to show there is never more than one. */
  function trackIntervals() {
    const started: unknown[] = [];
    const cleared: unknown[] = [];
    const realSet = window.setInterval.bind(window);
    const realClear = window.clearInterval.bind(window);
    spyOn(window, 'setInterval').and.callFake(((handler: TimerHandler, ms?: number) => {
      const id = realSet(handler, ms);
      // Only the popup's 1000 ms tick: RxJS schedules debounceTime on setInterval too, and those are not counted.
      if (ms === 1000) started.push(id);
      return id;
    }) as typeof window.setInterval);
    spyOn(window, 'clearInterval').and.callFake((id?: number) => {
      cleared.push(id);
      realClear(id);
    });
    return { active: () => started.filter(id => !cleared.includes(id)).length, started: () => started.length };
  }

  const runningSince = (msAgo: number): BulkExportStatus =>
    ({ ...inProgress, kickedOffOnUtc: new Date(Date.now() - msAgo).toISOString() });

  it('redraws the Elapsed of a running export every second, measured from Started', fakeAsync(() => {
    const intervals = trackIntervals();
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(runningSince(90_000)));
    open(component, 'run-a');
    fixture.detectChanges();

    // From Started, not from when the popup opened: 1m 30s straight away, not 0s.
    expect(elapsedText()).toBe('1m 30s');
    expect(window.setInterval).toHaveBeenCalledWith(jasmine.any(Function), 1000);

    tick(1000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 31s');
    tick(1000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 32s');
    tick(28_000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('2m 0s');
    expect(intervals.active()).toBe(1);

    component.closeBulkStatus();
    fixture.detectChanges();
    expect(intervals.active()).toBe(0);
    fixture.destroy();
  }));

  it('stops ticking and freezes at the final time as soon as the final status arrives', fakeAsync(() => {
    const intervals = trackIntervals();
    const { fixture, component } = mount();
    const running = runningSince(30_000);
    api.bulkExportStatus.and.returnValue(of(running));
    open(component, 'run-a');
    fixture.detectChanges();
    tick(5000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('35s');
    expect(intervals.active()).toBe(1);

    api.bulkExportStatus.and.returnValue(of({
      ...completed,
      kickedOffOnUtc: running.kickedOffOnUtc,
      transactionTime: new Date(Date.parse(running.kickedOffOnUtc) + 33_000).toISOString(),
      resourceTypes: [{ resourceType: 'Patient', fileCount: 1, state: 'Ready' as const }],
    }));
    events$.next(event('run-a'));
    tick(300); // the event debounce: the re-read lands here
    fixture.detectChanges();

    expect(elapsedText()).toBe('33s');          // Transaction time - Started, recalculated at once
    expect(popupText()).toContain('Completed');
    expect(popupText()).toContain('Ready');
    expect(intervals.active()).toBe(0);           // and the tick is gone

    tick(60_000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('33s');
    fixture.destroy();
  }));

  it('never ticks for an export that has already ended', fakeAsync(() => {
    const intervals = trackIntervals();
    const { fixture, component } = mount();
    for (const ended of [completed, { ...failed, completedOnUtc: '2026-10-13T09:27:16Z' }, { ...failed, status: 'Cancelled' as const }]) {
      api.bulkExportStatus.and.returnValue(of(ended));
      open(component, 'run-a');
      fixture.detectChanges();
      component.closeBulkStatus();
      fixture.detectChanges();
    }
    expect(intervals.started()).toBe(0);
    fixture.destroy();
  }));

  it('keeps one interval through refreshes that are still running, without resetting or jumping', fakeAsync(() => {
    const intervals = trackIntervals();
    const { fixture, component } = mount();
    const running = runningSince(10_000);
    api.bulkExportStatus.and.returnValue(of(running));
    open(component, 'run-a');
    fixture.detectChanges();

    tick(1000);
    events$.next(event('run-a', 'Running'));   // re-read, still running (a new object, same timestamps)
    tick(300);
    fixture.detectChanges();
    expect(elapsedText()).toBe('11s');
    tick(700);
    fixture.detectChanges();
    expect(elapsedText()).toBe('12s');

    expect(intervals.started()).toBe(1);
    expect(intervals.active()).toBe(1);
    fixture.destroy();
    expect(intervals.active()).toBe(0);
  }));

  it('moves its single interval with the popup when it is reopened on another running export', fakeAsync(() => {
    const intervals = trackIntervals();
    const { fixture, component } = mount();
    api.bulkExportStatus.and.callFake((id: string) =>
      of(id === 'run-a' ? runningSince(10_000) : runningSince(300_000)) as Observable<BulkExportStatus | null>);

    open(component, 'run-a');
    fixture.detectChanges();
    expect(elapsedText()).toBe('10s');
    component.closeBulkStatus();
    fixture.detectChanges();
    open(component, 'run-b');
    fixture.detectChanges();

    expect(elapsedText()).toBe('5m 0s');
    expect(intervals.active()).toBe(1);
    fixture.destroy();
  }));

  it('clears the interval when the page is left while an export is running', fakeAsync(() => {
    const intervals = trackIntervals();
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(runningSince(1000)));
    open(component, 'run-a');
    fixture.detectChanges();
    expect(intervals.active()).toBe(1);

    fixture.destroy();

    expect(intervals.active()).toBe(0);
  }));

  // ── Export Elapsed vs the run's Duration ─────────────────────────────────────────────────────────────────
  // They are different intervals by design: the run's Duration is WorkflowRun.StartedAt -> CompletedAt (the whole
  // workflow); the popup's Elapsed is the source's $export job, BulkExportJob.KickedOffOnUtc -> transactionTime.
  // Same interval -> same text; the reported run -> different, and the popup says which interval it shows.

  it('reads the same interval exactly as the run Duration does', () => {
    const { component } = mount();
    const start = '2026-10-13T12:05:24.924Z';
    for (const ms of [0, 45_400, 59_600, 85_861, 119_600, 126_568, 3_599_700]) {
      const end = new Date(Date.parse(start) + ms).toISOString();
      expect(component.bulkElapsed({ ...completed, kickedOffOnUtc: start, transactionTime: end }))
        .toBe(component.formatDuration(ms));
    }
  });

  it('shows the export interval, not the run interval, and says so', fakeAsync(() => {
    const { fixture, component } = mount();
    // The reported eCW run, as stored: run 12:05:24.924 -> 12:07:31.492; export 12:05:37.139 -> 12:07:03 (eCW).
    const runStartedAt = '2026-10-13T12:05:24.924188Z';
    const runCompletedAt = '2026-10-13T12:07:31.492939Z';
    const exportStatus: BulkExportStatus = {
      ...completed,
      kickedOffOnUtc: '2026-10-13T12:05:37.139033Z',
      transactionTime: '2026-10-13T12:07:03Z',
      completedOnUtc: '2026-10-13T12:07:25.416055Z',
    };
    api.bulkExportStatus.and.returnValue(of(exportStatus));
    open(component, 'run-a');
    fixture.detectChanges();

    const runDuration = component.formatDuration(Date.parse(runCompletedAt) - Date.parse(runStartedAt));
    expect(runDuration).toBe('2m 7s');
    expect(elapsedText()).toBe('1m 26s');
    // The export sits inside the run: kicked off after the run started, ended before the run finished.
    expect(Date.parse(exportStatus.kickedOffOnUtc)).toBeGreaterThan(Date.parse(runStartedAt));
    expect(Date.parse(exportStatus.completedOnUtc!)).toBeLessThan(Date.parse(runCompletedAt));

    const labels = Array.from(document.querySelectorAll('.bulk-status__facts dt')).map(dt => dt.textContent?.trim());
    expect(labels).toContain('Export started');
    expect(labels).toContain('Export elapsed');
    expect(labels).not.toContain('Started');
    expect(labels).not.toContain('Elapsed');
    expect(popupText()).toContain("Covers the source system's export only");
    fixture.destroy();
  }));

  it('ticks across the minute without skipping or showing 60s', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(runningSince(58_600)));
    open(component, 'run-a');
    fixture.detectChanges();
    expect(elapsedText()).toBe('59s');

    tick(1000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 0s');
    tick(1000);
    fixture.detectChanges();
    expect(elapsedText()).toBe('1m 1s');
    fixture.destroy();
  }));

  it('keeps the last read on screen and says so when a refresh fails', fakeAsync(() => {
    const { fixture, component } = mount();
    api.bulkExportStatus.and.returnValue(of(inProgress));
    open(component, 'run-a');

    api.bulkExportStatus.and.returnValue(throwError(() => new Error('source unreachable')));
    events$.next(event('run-a'));
    tick(1000);

    expect(component.bulkStatus()).toEqual(inProgress);
    expect(component.bulkStatusError()).toBeNull();
    expect(component.bulkStatusRefreshError()).toContain('Could not refresh');

    // The next successful read clears the note.
    api.bulkExportStatus.and.returnValue(of(completed));
    events$.next(event('run-a'));
    tick(1000);
    expect(component.bulkStatus()!.status).toBe('Completed');
    expect(component.bulkStatusRefreshError()).toBeNull();
    fixture.destroy();
  }));
});
