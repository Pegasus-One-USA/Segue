import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { CdsReadinessPanelComponent } from './cds-readiness-panel.component';
import { CdsReadiness } from '../../models/cds.models';
import { makeReadiness, makeReadinessCheck } from '../../testing/cds-test-data';

describe('CdsReadinessPanelComponent', () => {
  function render(readiness: CdsReadiness | null, loading = false): ComponentFixture<CdsReadinessPanelComponent> {
    TestBed.configureTestingModule({ imports: [CdsReadinessPanelComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(CdsReadinessPanelComponent);
    fixture.componentRef.setInput('readiness', readiness);
    fixture.componentRef.setInput('loading', loading);
    fixture.detectChanges();
    return fixture;
  }

  function el(fixture: ComponentFixture<CdsReadinessPanelComponent>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('says "Ready to go live" when every check is ok', () => {
    const fixture = render(makeReadiness());

    const summary = el(fixture).querySelector('[data-testid="cds-readiness-summary"]');
    expect(summary?.textContent).toContain('Ready to go live');
    expect(summary?.classList).toContain('summary--ok');
    expect(el(fixture).querySelector('[data-testid="cds-readiness-fix"]')).toBeNull();
  });

  it('colours each check by status and shows how to fix the ones that are not ok', () => {
    const fixture = render(makeReadiness({
      status: 'blocker',
      checks: [
        makeReadinessCheck(),
        makeReadinessCheck({
          key: 'replay-protection', label: 'Replay protection (Redis)', status: 'warning',
          detail: 'Memory only.', howToFix: 'Set ConnectionStrings:Redis.',
        }),
        makeReadinessCheck({
          key: 'trusted-issuer', label: 'Trusted EHR issuer', status: 'blocker',
          detail: 'No trusted issuer.', howToFix: 'Add the Epic issuer.',
        }),
      ],
    }));

    const rows = Array.from(el(fixture).querySelectorAll('[data-testid="cds-readiness-check"]'));
    expect(rows.length).toBe(3);
    expect(rows[0].classList).toContain('check--ok');
    expect(rows[1].classList).toContain('check--warning');
    expect(rows[1].querySelector('.cds-badge--warning')?.textContent).toContain('Review');
    expect(rows[2].classList).toContain('check--blocker');
    expect(rows[2].querySelector('.cds-badge--error')?.textContent).toContain('Blocker');
    expect(rows[2].querySelector('[data-testid="cds-readiness-fix"]')?.textContent).toContain('Add the Epic issuer.');

    const summary = el(fixture).querySelector('[data-testid="cds-readiness-summary"]');
    expect(summary?.classList).toContain('summary--blocker');
    expect(summary?.textContent).toContain('1 blocker to fix before go-live, and 1 item to review');
  });

  it('counts warnings when there is no blocker', () => {
    const fixture = render(makeReadiness({
      status: 'warning',
      checks: [makeReadinessCheck({ status: 'warning', howToFix: 'x' }), makeReadinessCheck({ key: 'b', status: 'warning', howToFix: 'y' })],
    }));

    expect(el(fixture).querySelector('[data-testid="cds-readiness-summary"]')?.textContent).toContain('No blockers. 2 items to review');
  });

  it('asks to try again when the report could not be loaded', () => {
    const fixture = render(null);
    expect(el(fixture).querySelector('.cds-notice--error')?.textContent).toContain('could not be loaded');
  });

  it('emits refresh from "Check again"', () => {
    const fixture = render(makeReadiness());
    let refreshed = 0;
    fixture.componentInstance.refresh.subscribe(() => refreshed++);

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="cds-readiness-refresh"]')!.click();

    expect(refreshed).toBe(1);
  });

  it('shows the coverage requirements (crd-*) checks like any other', () => {
    const fixture = render(makeReadiness({
      status: 'blocker',
      checks: [
        makeReadinessCheck({
          key: 'crd-payer-coverage', label: 'Coverage requirements payers', status: 'blocker',
          detail: 'A coverage requirements service is on but no payer is set up.', howToFix: 'Add each payer, then its rules.',
        }),
        makeReadinessCheck({ key: 'crd-rules-expiring', label: 'Coverage rules ending soon', status: 'warning', detail: '2 rules end within 30 days.', howToFix: 'Renew them.' }),
      ],
    }));
    const checks = el(fixture).querySelectorAll('[data-testid="cds-readiness-check"]');
    expect(checks[0].getAttribute('data-key')).toBe('crd-payer-coverage');
    expect(checks[0].textContent).toContain('no payer is set up');
    expect(checks[0].querySelector('[data-testid="cds-readiness-fix"]')?.textContent).toContain('Add each payer');
    expect(checks[1].classList).toContain('check--warning');
  });
});
