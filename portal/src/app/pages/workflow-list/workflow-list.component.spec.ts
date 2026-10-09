import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { NEVER, Subject, of } from 'rxjs';
import { WorkflowListComponent } from './workflow-list.component';
import { WorkflowApiService, WorkflowSummary } from '../../services/workflow-api.service';
import { RunStatusHubService } from '../../services/run-status-hub.service';
import { PermissionService } from '../../auth/services/permission.service';
import { EhrLiveWriteCheckService } from '../../services/ehr-live-write-check.service';
import { DialogService } from '../../core/services/dialog.service';

/**
 * Run on the workflow list: a workflow with a live EHR write-back that has not run yet asks first ("Write into Epic
 * now?"), because records written into an EHR cannot be taken back. A test run or a dry run runs straight away.
 */
describe('WorkflowListComponent — Run asks before a live EHR write', () => {
  let component: WorkflowListComponent;
  let api: { run: jasmine.Spy };
  let liveWriteCheck: { firstLiveWrite: jasmine.Spy };
  let dialogs: { open: jasmine.Spy };
  let closed: Subject<boolean | undefined>;

  const row = {
    workflowId: 'wf-1',
    name: 'Allergies to Epic',
    status: 'Ready',
    action: 'Run',
    modifiedOnUtc: '2026-10-01T00:00:00Z',
  } as unknown as WorkflowSummary;

  /** The live EHR the check reports, or null for a test run, a dry run, or a workflow that already ran as it is. */
  const create = (liveEhr: string | null) => {
    liveWriteCheck.firstLiveWrite.and.returnValue(of(liveEhr));
    component = TestBed.createComponent(WorkflowListComponent).componentInstance;
  };

  beforeEach(() => {
    api = { run: jasmine.createSpy('run').and.returnValue(of({ correlationId: 'c-1' })) };
    liveWriteCheck = { firstLiveWrite: jasmine.createSpy('firstLiveWrite') };
    closed = new Subject<boolean | undefined>();
    dialogs = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => closed.asObservable() }) };

    TestBed.configureTestingModule({
      imports: [WorkflowListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: WorkflowApiService, useValue: api },
        { provide: RunStatusHubService, useValue: { ensureConnected: () => undefined, runStatusChanged$: NEVER, reconnected$: NEVER } },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAll: () => true, hasAny: () => true } },
        { provide: EhrLiveWriteCheckService, useValue: liveWriteCheck },
        { provide: DialogService, useValue: dialogs },
      ],
    });
  });

  it('asks first for a live EHR workflow, and runs once Run is chosen', () => {
    create('Epic');

    component.onAction(row, 'async');

    expect(liveWriteCheck.firstLiveWrite).toHaveBeenCalledWith('wf-1', '2026-10-01T00:00:00Z');
    expect(dialogs.open).toHaveBeenCalledTimes(1);
    const data = dialogs.open.calls.mostRecent().args[1].data;
    expect(data.title).toBe('Write into Epic now?');
    expect(data.confirmLabel).toBe('Run');
    expect(data.danger).toBeTrue();
    expect(api.run).not.toHaveBeenCalled();

    closed.next(true);
    expect(api.run).toHaveBeenCalledOnceWith('wf-1', true);
  });

  it('Cancel stops the run', () => {
    create('Epic');

    component.onAction(row, 'async');
    closed.next(false);

    expect(dialogs.open).toHaveBeenCalledTimes(1);
    expect(api.run).not.toHaveBeenCalled();
    expect(component.busyId()).toBeNull();
  });

  it('a test-run or dry-run workflow runs without asking', () => {
    create(null);

    component.onAction(row, 'async');

    expect(dialogs.open).not.toHaveBeenCalled();
    expect(api.run).toHaveBeenCalledOnceWith('wf-1', true);
  });
});
