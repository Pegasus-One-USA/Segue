import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';
import { CdsActivityComponent } from './cds-activity.component';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsActivityPage, CdsCallLog } from '../../models/cds.models';
import { makeCdsApiSpy, makeService, makeToastSpy } from '../../testing/cds-test-data';

describe('CdsActivityComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;

  function log(overrides: Partial<CdsCallLog> = {}): CdsCallLog {
    return {
      id: 'l1', occurredOnUtc: '2026-10-10T12:00:00Z', kind: 'Invoke', serviceId: 'renal-dose-check',
      serviceInstanceId: 'svc-1', serviceTitle: 'Renal dose check', handlerKey: 'renal-dose', hook: 'order-sign',
      hookInstance: 'd1577c69-dfbe-44ad-ba6d-3e05e953b2ea', issuer: 'https://sandbox.cds-hooks.org',
      trustedIssuerId: 'iss-1', trustedIssuerName: 'CDS Hooks sandbox', outcome: 'Success', httpStatus: 200,
      cardCount: 2, durationMs: 87, detail: null, ...overrides,
    };
  }

  function create(items: CdsCallLog[] = [log()]) {
    api = makeCdsApiSpy();
    api.getServices.and.returnValue(of([makeService({ id: 'b', serviceId: 'zeta', title: 'Zeta' }), makeService()]));
    api.getActivity.and.returnValue(of({ items, totalCount: items.length, page: 1, pageSize: 25 }));
    toast = makeToastSpy();
    TestBed.configureTestingModule({
      imports: [CdsActivityComponent],
      providers: [provideNoopAnimations(), { provide: CdsApiService, useValue: api }, { provide: ToastService, useValue: toast }],
    });
    const fixture = TestBed.createComponent(CdsActivityComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const change = (sel: string, value: string) => {
      const input = el.querySelector(sel) as HTMLInputElement | HTMLSelectElement;
      input.value = value;
      input.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    return { fixture, page: fixture.componentInstance, el, change, lastFilter: () => api.getActivity.calls.mostRecent().args[0] };
  }

  it('loads the first page with no filters and renders each logged field', () => {
    const { el, lastFilter } = create([log(), log({ id: 'l2', kind: 'Feedback', outcome: 'Degraded', httpStatus: 200, detail: 'Missing prefetch: egfr', trustedIssuerName: null, issuer: null })]);

    expect(lastFilter()).toEqual({ serviceId: undefined, kind: undefined, outcome: undefined, fromUtc: undefined, toUtc: undefined, page: 1, pageSize: 25 });
    const rows = el.querySelectorAll('[data-testid="cds-activity-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Renal dose check');
    expect(rows[0].textContent).toContain('Order signed');
    expect(rows[0].textContent).toContain('d1577c69-dfbe-44ad-ba6d-3e05e953b2ea');
    expect(rows[0].textContent).toContain('CDS Hooks sandbox');
    expect(rows[0].textContent).toContain('Cards returned');
    expect(rows[0].textContent).toContain('87 ms');
    expect(rows[1].textContent).toContain('Feedback');
    expect(rows[1].textContent).toContain('Missing data');
    expect(rows[1].textContent).toContain('Missing prefetch: egfr');
    expect(rows[1].textContent).toContain('Unknown');
  });

  it('shows the Epic trigger, data fetched, fixes and dismiss reasons, and sums them above the table', () => {
    const { el } = create([
      log({ triggerAction: '5', epicVersion: 'Nov 2025', criteriaId: '777', callbackResourceCount: 2, callbackMs: 140, suggestionsOffered: 3 }),
      log({ id: 'l2', kind: 'Feedback', suggestionsAccepted: 1, overrideReasonCodes: ['will-monitor'] }),
      log({ id: 'l3', kind: 'Feedback', overrideReasonCodes: ['will-monitor'] }),
    ]);

    const rows = el.querySelectorAll('[data-testid="cds-activity-row"]');
    expect(rows[0].textContent).toContain('Epic trigger 5');
    expect(rows[0].textContent).toContain('Epic Nov 2025');
    expect(rows[0].textContent).toContain('Criteria 777');
    expect(rows[0].textContent).toContain('Fetched 2 from EHR in 140 ms');
    expect(rows[0].textContent).toContain('3 fixes offered');
    expect(rows[1].textContent).toContain('1 fix accepted');
    expect(rows[1].textContent).toContain('Dismissed: will-monitor');

    const summary = el.querySelector('[data-testid="cds-feedback-summary"]')!;
    expect(summary.textContent).toContain('3 fixes offered');
    expect(summary.textContent).toContain('1 fix accepted');
    expect(summary.textContent?.replace(/\s+/g, ' ')).toContain('will-monitor × 2');
  });

  it('shows the payer, coverage decisions and relay of a coverage requirements call', () => {
    const { el } = create([log({
      handlerKey: 'coverage-requirements', serviceTitle: 'Coverage requirements', payerName: 'Acme Health Plan',
      coverageDecisions: ['covered/auth-needed/-'], relayUsed: true, relayMs: 310, relayOutcome: 'ok',
      systemActionCount: 1, coverageAssertionCount: 1,
    })]);
    const row = el.querySelector('[data-testid="cds-activity-row"]')!;
    expect(row.textContent).toContain('Payer: Acme Health Plan');
    expect(row.textContent).toContain('Covered, prior authorization needed');
    expect(row.textContent).toContain('Asked the payer in 310 ms');
    expect(row.textContent).toContain('1 order updated');
    expect(row.textContent).toContain('1 coverage answer');
  });

  it('shows no extras or summary for plain calls', () => {
    const { el } = create();
    expect(el.querySelector('[data-testid="cds-call-coverage"]')).toBeNull();
    expect(el.querySelector('[data-testid="cds-call-extras"]')).toBeNull();
    expect(el.querySelector('[data-testid="cds-feedback-summary"]')).toBeNull();
  });

  it('lists services in the filter sorted by service id', () => {
    const { el } = create();
    const options = Array.from((el.querySelector('[data-testid="cds-activity-service"]') as HTMLSelectElement).options).map(o => o.value);
    expect(options).toEqual(['', 'renal-dose-check', 'zeta']);
  });

  it('applies each filter, going back to the first page', () => {
    const { page, change, lastFilter } = create();
    page.pageIndex.set(3);

    change('[data-testid="cds-activity-service"]', 'renal-dose-check');
    expect(lastFilter()).toEqual(jasmine.objectContaining({ serviceId: 'renal-dose-check', page: 1 }));

    change('[data-testid="cds-activity-outcome"]', 'Unauthorized');
    expect(lastFilter()).toEqual(jasmine.objectContaining({ serviceId: 'renal-dose-check', outcome: 'Unauthorized' }));

    change('select[aria-label="Type"]', 'Feedback');
    expect(lastFilter().kind).toBe('Feedback');
  });

  it('sends the date range as local start and end of day in UTC', () => {
    const { change, lastFilter } = create();

    change('.date-field:nth-of-type(1) input', '2026-10-01');
    change('.date-field:nth-of-type(2) input', '2026-10-02');

    expect(lastFilter().fromUtc).toBe(new Date(2026, 9, 1, 0, 0, 0, 0).toISOString());
    expect(lastFilter().toUtc).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).toISOString());
  });

  it('pages through results', () => {
    const { page, lastFilter } = create();
    page.onPageChange({ pageIndex: 2, pageSize: 50 });
    expect(lastFilter()).toEqual(jasmine.objectContaining({ page: 3, pageSize: 50 }));
  });

  it('resets every filter', () => {
    const { page, change, lastFilter, fixture, el } = create([]);
    change('[data-testid="cds-activity-outcome"]', 'Error');
    expect(el.textContent).toContain('No calls match your filters');

    page.reset();
    fixture.detectChanges();

    expect(lastFilter().outcome).toBeUndefined();
    expect(el.textContent).toContain('No calls yet');
  });

  it('labels an accepted feedback call "Feedback received", not "Cards returned"', () => {
    const { el } = create([log({ kind: 'Feedback', outcome: 'Success', cardCount: 1 })]);

    const row = el.querySelector('[data-testid="cds-activity-row"]')!;
    expect(row.textContent).toContain('Feedback received');
    expect(row.textContent).not.toContain('Cards returned');
  });

  it('drops a slower, older response once the filters change', () => {
    const { page, change } = create();
    const older = new Subject<CdsActivityPage>();
    const newer = new Subject<CdsActivityPage>();
    api.getActivity.and.returnValues(older, newer);

    change('[data-testid="cds-activity-outcome"]', 'Error');
    change('[data-testid="cds-activity-outcome"]', 'Forbidden');
    newer.next({ items: [log({ id: 'forbidden', outcome: 'Forbidden' })], totalCount: 1, page: 1, pageSize: 25 });
    older.next({ items: [log({ id: 'error', outcome: 'Error' })], totalCount: 9, page: 1, pageSize: 25 });

    expect(page.items().map(i => i.id)).toEqual(['forbidden']);
    expect(page.totalCount()).toBe(1);
  });

  it('shows an error toast when the log cannot load', () => {
    const { page } = create();
    api.getActivity.and.returnValue(throwError(() => new Error('down')));
    page.load();
    expect(toast.error).toHaveBeenCalledWith('Could not load CDS activity.');
    expect(page.loading()).toBeFalse();
  });
});
