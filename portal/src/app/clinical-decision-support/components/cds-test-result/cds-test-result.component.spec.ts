import { TestBed } from '@angular/core/testing';
import { CdsTestResultComponent } from './cds-test-result.component';
import { CdsTestResult } from '../../models/cds.models';
import { makeTestResult } from '../../testing/cds-test-data';

describe('CdsTestResultComponent', () => {
  function render(result: CdsTestResult) {
    TestBed.configureTestingModule({ imports: [CdsTestResultComponent] });
    const fixture = TestBed.createComponent(CdsTestResultComponent);
    fixture.componentRef.setInput('result', result);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the outcome, status, time and one preview per card', () => {
    const { el } = render(makeTestResult());

    expect(el.querySelector('[data-testid="cds-test-outcome"]')?.textContent).toContain('Cards returned');
    expect(el.textContent).toContain('HTTP 200');
    expect(el.textContent).toContain('42 ms');
    expect(el.textContent).toContain('1 card');
    expect(el.querySelectorAll('[data-testid="cds-card"]').length).toBe(1);
  });

  it('explains a no-cards answer and lists messages', () => {
    const { el } = render(makeTestResult({ outcome: 'NoCards', response: { cards: [] }, messages: ['No renal drugs in the order.'] }));

    expect(el.querySelectorAll('[data-testid="cds-card"]').length).toBe(0);
    expect(el.textContent).toContain('had no advice');
    expect(el.querySelector('.messages')?.textContent).toContain('No renal drugs in the order.');
  });

  it('handles a run with no response body', () => {
    const { el } = render(makeTestResult({ outcome: 'PreconditionFailed', httpStatus: 412, response: null }));

    expect(el.querySelector('[data-testid="cds-test-outcome"]')?.classList).toContain('cds-badge--warning');
    expect(el.textContent).toContain('0 cards');
  });

  it('toggles the raw response and the settings used', () => {
    const { fixture, el } = render(makeTestResult());
    expect(el.querySelector('pre')).toBeNull();

    (el.querySelector('.raw button') as HTMLButtonElement).click();
    fixture.detectChanges();

    const blocks = Array.from(el.querySelectorAll('pre')).map(p => p.textContent ?? '');
    expect(blocks.length).toBe(2);
    expect(blocks[0]).toContain('"summary"');
    expect(blocks[1]).toContain('"egfrThreshold": 30');
  });
});
