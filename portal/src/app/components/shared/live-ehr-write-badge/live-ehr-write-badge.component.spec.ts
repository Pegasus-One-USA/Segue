import { TestBed } from '@angular/core/testing';
import { LiveEhrWriteBadgeComponent } from './live-ehr-write-badge.component';

/** "Writes to <EHR names>" on a workflow that writes into an EHR for real; nothing for a test or dry run. */
describe('LiveEhrWriteBadgeComponent', () => {
  const render = (vendors: string[] | null) => {
    const fixture = TestBed.createComponent(LiveEhrWriteBadgeComponent);
    fixture.componentRef.setInput('vendors', vendors);
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector('[data-testid="live-ehr-write-badge"]');
  };

  it('names every EHR the workflow writes into live', () => {
    expect(render(['Epic'])?.textContent?.trim()).toBe('Writes to Epic');
    expect(render(['Healow', 'Athenahealth'])?.textContent?.trim()).toBe('Writes to eClinicalWorks and athenahealth');
  });

  it('shows nothing when no write-back is live (test or dry run only)', () => {
    expect(render([])).toBeNull();
    expect(render(null)).toBeNull();
  });
});
