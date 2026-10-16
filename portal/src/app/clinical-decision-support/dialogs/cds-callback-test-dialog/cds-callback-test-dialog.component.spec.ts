import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { CdsCallbackTestDialogComponent } from './cds-callback-test-dialog.component';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsCallbackTestResult } from '../../models/cds.models';
import { makeCdsApiSpy, makeIssuer } from '../../testing/cds-test-data';

describe('CdsCallbackTestDialogComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let ref: { close: jasmine.Spy };

  function result(overrides: Partial<CdsCallbackTestResult> = {}): CdsCallbackTestResult {
    return {
      trustedIssuerId: 'iss-1', fhirBaseUrl: 'https://ehr.example.org', allowCallback: true, reachable: true,
      httpStatus: 200, fhirVersion: '4.0.1', software: 'Epic', durationMs: 120, message: 'The FHIR server answered.',
      ...overrides,
    };
  }

  function create(answer: ReturnType<CdsApiService['testCallback']>) {
    api = makeCdsApiSpy();
    api.testCallback.and.returnValue(answer);
    ref = { close: jasmine.createSpy('close') };
    TestBed.configureTestingModule({
      imports: [CdsCallbackTestDialogComponent],
      providers: [
        { provide: CdsApiService, useValue: api },
        { provide: DialogRef, useValue: ref },
        { provide: DIALOG_DATA, useValue: { issuer: makeIssuer() } },
      ],
    });
    const fixture = TestBed.createComponent(CdsCallbackTestDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const again = () => Array.from(el.querySelectorAll('button')).find(b => b.textContent?.includes('Test again')) as HTMLButtonElement;
    return { fixture, dialog: fixture.componentInstance, el, again };
  }

  it('runs once on open and shows "Testing..." with Test again disabled until the answer comes', () => {
    const pending = new Subject<CdsCallbackTestResult>();
    const { el, fixture, again } = create(pending);

    expect(api.testCallback).toHaveBeenCalledOnceWith('iss-1');
    expect(el.querySelector('[data-testid="cds-callback-test-running"]')).not.toBeNull();
    expect(again().disabled).toBeTrue();

    pending.next(result());
    pending.complete();
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="cds-callback-test-running"]')).toBeNull();
    expect(again().disabled).toBeFalse();
  });

  it('shows a reachable server with its status, version and software', () => {
    const { el } = create(of(result()));
    const notice = el.querySelector('[data-testid="cds-callback-test-result"]')!;
    expect(notice.classList).not.toContain('cds-notice--error');
    expect(notice.textContent).toContain('The FHIR server answered.');
    expect(el.textContent).toContain('HTTP 200 in 120 ms');
    expect(el.textContent).toContain('4.0.1');
    expect(el.textContent).toContain('Allowed');
  });

  it('flags an unreachable server', () => {
    const { el } = create(of(result({ reachable: false, httpStatus: null, fhirVersion: null, software: null, message: 'Timed out.' })));
    const notice = el.querySelector('[data-testid="cds-callback-test-result"]')!;
    expect(notice.classList).toContain('cds-notice--error');
    expect(el.textContent).toContain('No answer in 120 ms');
    expect(el.textContent).not.toContain('FHIR version');
  });

  it('points out that fetching is off for this issuer even though the server answers', () => {
    const { el } = create(of(result({ allowCallback: false })));
    expect(el.textContent).toContain('Off for this issuer');
    expect(el.textContent).toContain("doesn't let Segue fetch missing data");
  });

  it('shows the API message when the test cannot run, and runs again on request', () => {
    const { el, fixture, again } = create(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 403 }), { message: 'Missing cdshooks.test.' })));
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Missing cdshooks.test.');

    api.testCallback.and.returnValue(of(result()));
    again().click();
    fixture.detectChanges();

    expect(api.testCallback).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[role="alert"]')).toBeNull();
    expect(el.querySelector('[data-testid="cds-callback-test-result"]')).not.toBeNull();
  });

  it('closes', () => {
    const { el } = create(of(result()));
    (el.querySelector('.btn-primary') as HTMLButtonElement).click();
    expect(ref.close).toHaveBeenCalled();
  });
});
