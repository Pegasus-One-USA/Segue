import { TestBed } from '@angular/core/testing';
import { CdsFeedbackSummaryComponent } from './cds-feedback-summary.component';
import { CdsCallLog } from '../../models/cds.models';
import { makeCallLog } from '../../testing/cds-test-data';

describe('CdsFeedbackSummaryComponent', () => {
  function render(calls: CdsCallLog[]): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsFeedbackSummaryComponent] });
    const fixture = TestBed.createComponent(CdsFeedbackSummaryComponent);
    fixture.componentRef.setInput('calls', calls);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows nothing without fixes or dismiss reasons', () => {
    expect(render([makeCallLog(), makeCallLog({ suggestionsOffered: 0 })]).querySelector('[data-testid="cds-feedback-summary"]')).toBeNull();
  });

  it('totals fixes and counts each reason code, most picked first', () => {
    const el = render([
      makeCallLog({ suggestionsOffered: 2 }),
      makeCallLog({ suggestionsOffered: 1 }),
      makeCallLog({ kind: 'Feedback', suggestionsAccepted: 1, overrideReasonCodes: ['will-monitor'] }),
      makeCallLog({ kind: 'Feedback', overrideReasonCodes: ['not-applicable', 'will-monitor'] }),
    ]);

    const items = Array.from(el.querySelectorAll('.summary__item')).map(i => (i.textContent ?? '').replace(/\s+/g, ' ').trim());
    expect(items).toEqual(['3 fixes offered', '1 fix accepted', 'will-monitor × 2', 'not-applicable × 1']);
  });

  it('counts every item of a batched Epic feedback call', () => {
    const el = render([
      makeCallLog({
        kind: 'Feedback',
        overrideReasonCodes: ['will-monitor', '45'],
        overrideReasonCounts: [{ code: 'will-monitor', count: 5 }, { code: '45', count: 1 }],
      }),
      makeCallLog({ kind: 'Feedback', overrideReasonCodes: ['will-monitor'] }),
    ]);

    const items = Array.from(el.querySelectorAll('.summary__item')).map(i => (i.textContent ?? '').replace(/\s+/g, ' ').trim());
    expect(items).toEqual(['will-monitor × 6', '45 × 1']);
  });
});
