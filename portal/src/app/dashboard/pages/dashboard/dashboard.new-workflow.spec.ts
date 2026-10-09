import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router, provideRouter } from '@angular/router';
import { EMPTY, of } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { DashboardService } from '../../services/dashboard.service';
import { PipelineRunService } from '../../services/pipeline-run.service';
import { ExecutionHistoryApiService } from '../../../execution-history/services/execution-history-api.service';
import { RunStatusHubService } from '../../../services/run-status-hub.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { WorkflowApiService } from '../../../services/workflow-api.service';

/** The Dashboard's New used to link straight to an empty, unsaved builder. It now opens the same "New workflow"
 *  dialog as the Workflows list, which creates the workflow first. */
describe('Dashboard — New workflow', () => {
  function mount(canCreate = true) {
    TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideRouter([]),
        { provide: DashboardService, useValue: { lastRefreshed: signal(new Date()), refresh: () => undefined } },
        { provide: PipelineRunService, useValue: { runs: signal([]), fetchRecent: () => undefined } },
        { provide: ExecutionHistoryApiService, useValue: { statusCounts: () => EMPTY } },
        { provide: RunStatusHubService, useValue: { ensureConnected: () => undefined, runStatusChanged$: EMPTY, reconnected$: EMPTY } },
        { provide: PermissionService, useValue: { hasPermission: () => canCreate } },
        { provide: WorkflowApiService, useValue: { save: () => of({ id: 'wf-1' }) } },
      ],
    });
    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('New opens the New workflow dialog on the Dashboard, without navigating', () => {
    const { fixture, el } = mount();
    const navigate = spyOn(TestBed.inject(Router), 'navigateByUrl').and.callThrough();
    const button = el.querySelector<HTMLButtonElement>('.new-pipeline-btn')!;
    expect(button.tagName).toBe('BUTTON');
    expect(el.querySelector('app-new-workflow-dialog')).toBeNull();

    button.click();
    fixture.detectChanges();
    expect(el.querySelector('app-new-workflow-dialog #new-workflow-title')?.textContent).toBe('New workflow');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('closing the dialog removes it', () => {
    const { fixture, el } = mount();
    el.querySelector<HTMLButtonElement>('.new-pipeline-btn')!.click();
    fixture.detectChanges();
    el.querySelector<HTMLButtonElement>('app-new-workflow-dialog .modal-close')!.click();
    fixture.detectChanges();
    expect(el.querySelector('app-new-workflow-dialog')).toBeNull();
  });

  it('is still hidden without permission to create workflows', () => {
    const { el } = mount(false);
    expect(el.querySelector('.new-pipeline-btn')).toBeNull();
  });
});
