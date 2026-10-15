import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { WorkflowListComponent } from './workflow-list.component';
import { WorkflowApiService } from '../../services/workflow-api.service';
import { ToastService } from '../../services/toast.service';
import { RunStatusHubService } from '../../services/run-status-hub.service';
import { PermissionService } from '../../auth/services/permission.service';
import { AuthStore } from '../../auth/store/auth.store';
import { PhaseConfigService } from '../../services/phase-config.service';

/**
 * The builder's Save lands on the list with ?search=<name>&highlight=<id>. The list must open showing just that
 * workflow — searched by its unique number, since a name like "a" substring-matches unrelated workflows — flash its
 * row, and drop the params so a refresh shows everything again.
 */
describe('WorkflowList — arriving from a builder Save', () => {
  let summary: jasmine.Spy;
  let load: jasmine.Spy;
  let router: Router;

  const emptyPage = {
    items: [], totalCount: 0, availableStatuses: [], availableApplicationTypes: [], availableLastRunStatuses: [],
  };

  function create(queryParams: Record<string, string>): WorkflowListComponent {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } } },
        { provide: WorkflowApiService, useValue: { summary, load } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show', 'success', 'error', 'warning']) },
        { provide: RunStatusHubService, useValue: {
            ensureConnected: () => undefined, runStatusChanged$: new Subject(), reconnected$: new Subject() } },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: AuthStore, useValue: { hasRole: () => false } },
        { provide: PhaseConfigService, useValue: { isSourceEnabled: () => true, isTransformEnabled: () => true } },
      ],
    });
    TestBed.overrideComponent(WorkflowListComponent, { set: { template: '', imports: [] } });
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    const list = TestBed.createComponent(WorkflowListComponent).componentInstance;
    list.ngOnInit();
    return list;
  }

  const firstSearch = () => summary.calls.first().args[0];

  beforeEach(() => {
    summary = jasmine.createSpy('summary').and.returnValue(of(emptyPage));
    load = jasmine.createSpy('load').and.returnValue(of({ id: 'wf-123', name: 'a', workflowNumber: 'WLW-141026-0001' }));
  });

  it('searches by the saved workflow\'s unique number, not its name, so similar names are not pulled in', () => {
    const list = create({ search: 'a', highlight: 'wf-123' });

    expect(load).toHaveBeenCalledWith('wf-123');
    expect(list.searchQuery()).toBe('WLW-141026-0001');
    expect(summary).toHaveBeenCalledTimes(1);
    expect(firstSearch()).toEqual(jasmine.objectContaining({ page: 1, search: 'WLW-141026-0001' }));
  });

  it('falls back to the name for a workflow without a number', () => {
    load.and.returnValue(of({ id: 'wf-123', name: 'Legacy flow', workflowNumber: null }));

    const list = create({ search: 'Legacy flow', highlight: 'wf-123' });

    expect(list.searchQuery()).toBe('Legacy flow');
    expect(firstSearch().search).toBe('Legacy flow');
  });

  it('falls back to the name when the lookup fails, rather than showing nothing', () => {
    load.and.returnValue(throwError(() => new Error('404')));

    const list = create({ search: 'Legacy flow', highlight: 'wf-123' });

    expect(list.searchQuery()).toBe('Legacy flow');
    expect(summary).toHaveBeenCalledTimes(1);
  });

  it('strips the params from the URL without adding a history entry', () => {
    create({ search: 'a', highlight: 'wf-123' });

    expect(router.navigate).toHaveBeenCalledWith([], jasmine.objectContaining({ queryParams: {}, replaceUrl: true }));
  });

  it('highlights the saved row, then clears it after the rows have been shown for a while', fakeAsync(() => {
    const list = create({ search: 'a', highlight: 'wf-123' });

    expect(list.highlightedId()).toBe('wf-123');
    tick(3000);
    expect(list.highlightedId()).toBeNull();
  }));

  it('opens the full, unfiltered list when reached any other way', () => {
    const list = create({});

    expect(load).not.toHaveBeenCalled();
    expect(list.searchQuery()).toBe('');
    expect(list.highlightedId()).toBeNull();
    expect(firstSearch().search).toBeUndefined();
    expect(router.navigate).not.toHaveBeenCalled();
  });
});
