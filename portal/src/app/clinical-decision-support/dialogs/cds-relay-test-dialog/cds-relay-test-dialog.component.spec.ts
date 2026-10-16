import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { CdsRelayTestDialogComponent } from './cds-relay-test-dialog.component';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CdsPayerRelayTestResult } from '../../models/cds-coverage.models';
import { makeCoverageApiSpy, makePayer, makeRelayTestResult } from '../../testing/cds-test-data';

describe('CdsRelayTestDialogComponent', () => {
  let api: ReturnType<typeof makeCoverageApiSpy>;
  let ref: { close: jasmine.Spy };

  function create(answer: ReturnType<CdsCoverageApiService['testRelay']>) {
    api = makeCoverageApiSpy();
    api.testRelay.and.returnValue(answer);
    ref = { close: jasmine.createSpy('close') };
    TestBed.configureTestingModule({
      imports: [CdsRelayTestDialogComponent],
      providers: [
        { provide: CdsCoverageApiService, useValue: api },
        { provide: DialogRef, useValue: ref },
        { provide: DIALOG_DATA, useValue: { payer: makePayer({ relayEnabled: true }) } },
      ],
    });
    const fixture = TestBed.createComponent(CdsRelayTestDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const again = () => Array.from(el.querySelectorAll('button')).find(b => b.textContent?.includes('Test again')) as HTMLButtonElement;
    return { fixture, el, again };
  }

  it('runs on open and disables Test again until the answer comes', () => {
    const pending = new Subject<CdsPayerRelayTestResult>();
    const { el, fixture, again } = create(pending);
    expect(api.testRelay).toHaveBeenCalledOnceWith('payer-1');
    expect(el.querySelector('[data-testid="cds-relay-test-running"]')).not.toBeNull();
    expect(again().disabled).toBeTrue();
    pending.next(makeRelayTestResult());
    pending.complete();
    fixture.detectChanges();
    expect(again().disabled).toBeFalse();
  });

  it('shows the outcome, status, time and card and order update counts', () => {
    const { el } = create(of(makeRelayTestResult({ cardCount: 2, systemActionCount: 3 })));
    expect(el.querySelector('[data-testid="cds-relay-test-outcome"]')?.textContent).toContain('Answered');
    expect(el.textContent).toContain('HTTP 200 in 180 ms');
    const facts = Array.from(el.querySelectorAll('.facts dd')).map(d => d.textContent?.trim());
    expect(facts).toContain('2');
    expect(facts).toContain('3');
    expect(el.querySelector('[data-testid="cds-relay-test-result"]')?.classList).not.toContain('cds-notice--error');
  });

  it('flags a payer that did not answer', () => {
    const { el } = create(of(makeRelayTestResult({
      reachable: false, httpStatus: null, outcome: 'timed out', cardCount: 0, systemActionCount: 0, message: 'The payer did not answer in time.',
    })));
    expect(el.querySelector('[data-testid="cds-relay-test-result"]')?.classList).toContain('cds-notice--error');
    expect(el.querySelector('[data-testid="cds-relay-test-outcome"]')?.textContent).toContain('Timed out');
    expect(el.textContent).toContain('No answer in 180 ms');
  });

  it('shows the API message when the test cannot run, and runs again on request', () => {
    const { el, fixture, again } = create(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 404 }), { message: 'The payer no longer exists.' })));
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('The payer no longer exists.');
    api.testRelay.and.returnValue(of(makeRelayTestResult()));
    again().click();
    fixture.detectChanges();
    expect(api.testRelay).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[role="alert"]')).toBeNull();
  });

  it('closes', () => {
    const { el } = create(of(makeRelayTestResult()));
    (el.querySelector('.btn-primary') as HTMLButtonElement).click();
    expect(ref.close).toHaveBeenCalled();
  });
});
