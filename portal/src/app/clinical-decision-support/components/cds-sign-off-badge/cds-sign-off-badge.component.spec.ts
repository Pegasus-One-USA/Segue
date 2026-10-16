import { TestBed } from '@angular/core/testing';
import { CdsSignOffBadgeComponent } from './cds-sign-off-badge.component';

describe('CdsSignOffBadgeComponent', () => {
  function render(signedOffBy: string | null, signedOffAtUtc: string | null): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsSignOffBadgeComponent] });
    const fixture = TestBed.createComponent(CdsSignOffBadgeComponent);
    fixture.componentRef.setInput('signedOffBy', signedOffBy);
    fixture.componentRef.setInput('signedOffAtUtc', signedOffAtUtc);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows who reviewed the service and when', () => {
    const badge = render('Dr A. Patel', '2026-10-15T14:00:00Z').querySelector('[data-testid="cds-sign-off-badge"]');
    expect(badge?.textContent).toContain('Reviewed by Dr A. Patel on Oct 15, 2026');
    expect(badge?.classList).toContain('cds-badge--success');
  });

  it('says "Not clinically reviewed" when there is no sign-off', () => {
    const badge = render(null, null).querySelector('[data-testid="cds-sign-off-badge"]');
    expect(badge?.textContent).toContain('Not clinically reviewed');
    expect(badge?.classList).toContain('cds-badge--warning');
  });
});
