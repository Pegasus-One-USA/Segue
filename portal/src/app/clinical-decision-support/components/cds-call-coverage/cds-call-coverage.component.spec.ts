import { TestBed } from '@angular/core/testing';
import { CdsCallCoverageComponent } from './cds-call-coverage.component';
import { CdsCallLog } from '../../models/cds.models';
import { makeCallLog } from '../../testing/cds-test-data';

describe('CdsCallCoverageComponent', () => {
  function render(call: CdsCallLog) {
    TestBed.configureTestingModule({ imports: [CdsCallCoverageComponent] });
    const fixture = TestBed.createComponent(CdsCallCoverageComponent);
    fixture.componentRef.setInput('call', call);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      el,
      decisions: Array.from(el.querySelectorAll('.decision')).map(e => e.textContent?.trim()),
      facts: Array.from(el.querySelectorAll('.fact')).map(e => e.textContent?.trim()),
    };
  }

  it('shows nothing for a call of another service', () => {
    expect(render(makeCallLog()).el.querySelector('[data-testid="cds-call-coverage"]')).toBeNull();
  });

  it('shows the payer, each decision in plain words, and the orders updated', () => {
    const { el, decisions, facts } = render(makeCallLog({
      handlerKey: 'coverage-requirements', payerName: 'Acme Health Plan',
      coverageDecisions: ['covered/auth-needed/clinical', 'not-covered/-/-'],
      relayUsed: false, systemActionCount: 2, coverageAssertionCount: 2,
    }));
    expect(el.textContent).toContain('Payer: Acme Health Plan');
    expect(decisions).toEqual(['Covered, prior authorization needed, clinical documentation', 'Not covered']);
    expect(facts).toEqual(['2 orders updated', '2 coverage answers']);
  });

  it('says when no payer matched', () => {
    const { el } = render(makeCallLog({ handlerKey: 'coverage-requirements' }));
    expect(el.textContent).toContain('No payer matched');
  });

  it('shows a relay that answered, and one that failed', () => {
    expect(render(makeCallLog({ payerName: 'Beta', relayUsed: true, relayMs: 240, relayOutcome: 'ok', systemActionCount: 1 })).facts)
      .toEqual(['Asked the payer in 240 ms', '1 order updated']);
    TestBed.resetTestingModule();
    const failed = render(makeCallLog({ payerName: 'Beta', relayUsed: true, relayMs: 900, relayOutcome: 'timed out; local rules used' }));
    expect(failed.facts).toEqual(['Payer: timed out; local rules used in 900 ms']);
    expect(failed.el.querySelector('.fact--warning')).not.toBeNull();
  });

  it('does not treat feedback on a coverage service as a coverage call', () => {
    expect(render(makeCallLog({ kind: 'Feedback', handlerKey: 'coverage-requirements' })).el.querySelector('[data-testid="cds-call-coverage"]')).toBeNull();
  });
});
