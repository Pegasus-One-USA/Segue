import { TestBed } from '@angular/core/testing';
import { CdsCallExtrasComponent } from './cds-call-extras.component';
import { CdsCallLog } from '../../models/cds.models';
import { makeCallLog } from '../../testing/cds-test-data';

describe('CdsCallExtrasComponent', () => {
  function labels(call: CdsCallLog): string[] {
    TestBed.configureTestingModule({ imports: [CdsCallExtrasComponent] });
    const fixture = TestBed.createComponent(CdsCallExtrasComponent);
    fixture.componentRef.setInput('call', call);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return Array.from(el.querySelectorAll('.extra')).map(e => e.textContent?.trim() ?? '');
  }

  it('shows nothing for a plain call', () => {
    expect(labels(makeCallLog())).toEqual([]);
  });

  it('shows the Epic trigger, version, criteria, data fetched and fixes offered', () => {
    expect(labels(makeCallLog({
      triggerAction: '5', epicVersion: 'Nov 2025', criteriaId: '12345', callbackResourceCount: 3, callbackMs: 210, suggestionsOffered: 2,
    }))).toEqual(['Epic trigger 5', 'Epic Nov 2025', 'Criteria 12345', 'Fetched 3 from EHR in 210 ms', '2 fixes offered']);
  });

  it('shows a fetch that found nothing, without a time when none was recorded', () => {
    expect(labels(makeCallLog({ callbackResourceCount: 0, callbackMs: null }))).toEqual(['Fetched 0 from EHR']);
  });

  it('shows fixes accepted and each dismiss reason code on feedback', () => {
    expect(labels(makeCallLog({
      kind: 'Feedback', suggestionsAccepted: 1, overrideReasonCodes: ['will-monitor', 'not-applicable'],
    }))).toEqual(['1 fix accepted', 'Dismissed: will-monitor', 'Dismissed: not-applicable']);
  });

  it('shows how many cards of a batched feedback call carried each reason', () => {
    expect(labels(makeCallLog({
      kind: 'Feedback',
      overrideReasonCodes: ['will-monitor', '45'],
      overrideReasonCounts: [{ code: 'will-monitor', count: 5 }, { code: '45', count: 1 }],
    }))).toEqual(['Dismissed: will-monitor ×5', 'Dismissed: 45']);
  });
});
