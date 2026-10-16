import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { AuditLogsComponent, DEFAULT_PERIOD } from './audit-logs.component';
import { GovernanceApiService } from '../../services/governance-api.service';
import { DialogService } from '../../../core/services/dialog.service';
import { AuditLogEntry, AuditLogFilter } from '../../models/governance.model';

describe('AuditLogsComponent filters', () => {
  let api: jasmine.SpyObj<GovernanceApiService>;
  let component: AuditLogsComponent;

  /** The filter argument of the most recent auditLogs() call. */
  const lastFilter = (): AuditLogFilter | undefined =>
    api.auditLogs.calls.mostRecent().args[7] as AuditLogFilter | undefined;

  beforeEach(() => {
    api = jasmine.createSpyObj<GovernanceApiService>('GovernanceApiService', ['auditLogs', 'auditLogFilterOptions']);
    api.auditLogs.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 10 }));
    api.auditLogFilterOptions.and.returnValue(of({
      modules: ['Role', 'User'],
      actors: [{ value: 'u-1', name: 'Admin Demo' }, { value: 'anonymous', name: 'anonymous' }],
    }));

    TestBed.configureTestingModule({
      imports: [AuditLogsComponent],
      providers: [
        { provide: GovernanceApiService, useValue: api },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: Router, useValue: jasmine.createSpyObj<Router>('Router', ['navigate']) },
        { provide: DialogService, useValue: jasmine.createSpyObj<DialogService>('DialogService', ['open']) },
      ],
    });

    component = TestBed.createComponent(AuditLogsComponent).componentInstance;
    component.ngOnInit();
  });

  const DAY_MS = 24 * 60 * 60 * 1000;

  it('opens on the last 24 hours, with no "any time" choice', () => {
    expect(DEFAULT_PERIOD).toBe('24h');
    expect(component.period()).toBe('24h');

    const from = new Date(lastFilter()!.fromUtc!).getTime();
    expect(Date.now() - from).toBeGreaterThanOrEqual(DAY_MS - 1000);
    expect(Date.now() - from).toBeLessThanOrEqual(DAY_MS + 1000);
    expect(component.dirty()).toBeFalse();
  });

  it('loads the dropdown options once', () => {
    expect(api.auditLogFilterOptions).toHaveBeenCalledTimes(1);
    expect(component.filterOptions().actors.map(a => a.name)).toEqual(['Admin Demo', 'anonymous']);
  });

  it('does not reload while filters are only being picked, and shows that Search is pending', () => {
    const callsBefore = api.auditLogs.calls.count();

    component.onFilterChange('actor', 'u-1');
    component.onPeriodChange('7d');

    expect(api.auditLogs.calls.count()).toBe(callsBefore);
    expect(component.dirty()).toBeTrue();
  });

  it('applies actor, module, action and correlation id on Search, from the first page', () => {
    component.pageIndex.set(3);
    component.onCorrelationIdChange(' corr-1 ');
    component.onFilterChange('actor', 'u-1');
    component.onFilterChange('module', 'Role');
    component.onFilterChange('action', 'Deleted');

    component.search();

    expect(lastFilter()).toEqual(jasmine.objectContaining({ actor: 'u-1', module: 'Role', action: 'Deleted' }));
    expect(api.auditLogs.calls.mostRecent().args[0]).toBe('corr-1');
    expect(component.pageIndex()).toBe(0);
    expect(component.dirty()).toBeFalse();
  });

  it('keeps paging on the searched filters, not on ones picked afterwards', () => {
    component.onFilterChange('module', 'Role');
    component.search();
    component.onFilterChange('module', 'User');

    component.onPageChange({ pageIndex: 1, pageSize: 10 });

    expect(lastFilter()!.module).toBe('Role');
  });

  it('turns a preset period into a from-time that far back', () => {
    component.onPeriodChange('7d');
    const before = Date.now();
    component.search();

    const from = new Date(lastFilter()!.fromUtc!).getTime();
    expect(before - from).toBeGreaterThanOrEqual(7 * 24 * 60 * 60 * 1000 - 1000);
    expect(before - from).toBeLessThanOrEqual(7 * 24 * 60 * 60 * 1000 + 1000);
    expect(lastFilter()!.toUtc).toBeUndefined();
  });

  it('keeps a preset period on the same time window while paging later', () => {
    component.onPeriodChange('7d');
    component.search();
    const searchedFrom = lastFilter()!.fromUtc;

    jasmine.clock().install();
    try {
      jasmine.clock().mockDate(new Date(Date.now() + 5 * 60 * 1000));
      component.onPageChange({ pageIndex: 1, pageSize: 10 });
      component.onSort('module');
    } finally {
      jasmine.clock().uninstall();
    }

    expect(lastFilter()!.fromUtc).toBe(searchedFrom);
  });

  it('will not search a custom range without a From date, and says why', () => {
    const callsBefore = api.auditLogs.calls.count();
    component.onPeriodChange('custom');
    component.onFilterChange('toDate', '2026-10-03');

    component.search();

    expect(api.auditLogs.calls.count()).toBe(callsBefore);
    expect(component.filterError()).toContain('From date');

    component.onFilterChange('fromDate', '2026-10-01');
    expect(component.filterError()).toBe('');
  });

  it('treats a custom range as whole local days, the To day included', () => {
    component.onPeriodChange('custom');
    component.onFilterChange('fromDate', '2026-10-01');
    component.onFilterChange('toDate', '2026-10-03');
    component.search();

    expect(lastFilter()!.fromUtc).toBe(new Date(2026, 9, 1).toISOString());
    expect(lastFilter()!.toUtc).toBe(new Date(2026, 9, 4).toISOString());
  });

  it('clears every filter when opening an entity version history, and sends none', () => {
    component.onFilterChange('actor', 'u-1');
    component.onPeriodChange('30d');
    component.search();

    component.viewEntityHistory({ entityType: 'Role', entityId: 'r-1' } as AuditLogEntry);

    expect(component.actor()).toBe('');
    expect(component.period()).toBe(DEFAULT_PERIOD);
    expect(lastFilter()).toBeUndefined();
  });

  it('reset clears the filter bar and goes back to the last 24 hours', () => {
    component.onFilterChange('module', 'User');
    component.onPeriodChange('custom');
    component.onFilterChange('fromDate', '2026-10-01');
    component.search();

    component.reset();

    expect(component.module()).toBe('');
    expect(component.period()).toBe(DEFAULT_PERIOD);
    expect(component.fromDate()).toBe('');
    expect(lastFilter()!.module).toBeUndefined();
    expect(Date.now() - new Date(lastFilter()!.fromUtc!).getTime()).toBeLessThanOrEqual(DAY_MS + 1000);
    expect(lastFilter()!.toUtc).toBeUndefined();
    expect(component.dirty()).toBeFalse();
  });

  it('searches a correlation ID across all dates, ignoring the period', () => {
    component.onPeriodChange('7d');
    component.onCorrelationIdChange('corr-old');

    component.search();

    expect(component.correlationIgnoresPeriod()).toBeTrue();
    expect(api.auditLogs.calls.mostRecent().args[0]).toBe('corr-old');
    expect(lastFilter()!.fromUtc).toBeUndefined();
    expect(lastFilter()!.toUtc).toBeUndefined();
  });

  it('will not search a custom range whose To date is before its From date', () => {
    const callsBefore = api.auditLogs.calls.count();
    component.onPeriodChange('custom');
    component.onFilterChange('fromDate', '2026-10-05');
    component.onFilterChange('toDate', '2026-10-01');

    component.search();

    expect(api.auditLogs.calls.count()).toBe(callsBefore);
    expect(component.filterError()).toContain('on or after');
  });
});
