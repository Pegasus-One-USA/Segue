import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { CdsDiscoveryPanelComponent } from './cds-discovery-panel.component';
import { CdsDiscoveryInfo } from '../../models/cds.models';
import { makeDiscovery } from '../../testing/cds-test-data';

describe('CdsDiscoveryPanelComponent', () => {
  function render(info: CdsDiscoveryInfo): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsDiscoveryPanelComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(CdsDiscoveryPanelComponent);
    fixture.componentRef.setInput('info', info);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the discovery URL and no warning when the public base URL is set', () => {
    const el = render(makeDiscovery());

    expect(el.querySelector('[data-testid="cds-discovery-url"]')?.textContent).toBe('https://segue.example.org/cds-services');
    expect(el.querySelector('.cds-notice--warning')).toBeNull();
  });

  it('warns and names the setting when the public base URL is not configured', () => {
    const el = render(makeDiscovery({ isConfigured: false }));
    expect(el.querySelector('.cds-notice--warning')?.textContent).toContain('Cds:PublicBaseUrl');
  });

  it('lists each service URL with its on/off state', () => {
    const el = render(makeDiscovery({
      services: [
        { id: '1', serviceId: 'a', title: 'Allergy', hook: 'order-sign', isEnabled: true,
          serviceUrl: 'https://segue.example.org/cds-services/a', feedbackUrl: 'https://segue.example.org/cds-services/a/feedback' },
        { id: '2', serviceId: 'b', title: 'Gaps', hook: 'patient-view', isEnabled: false,
          serviceUrl: 'https://segue.example.org/cds-services/b', feedbackUrl: 'https://segue.example.org/cds-services/b/feedback' },
      ],
    }));

    const rows = Array.from(el.querySelectorAll('.service'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('https://segue.example.org/cds-services/a');
    expect(rows[1].querySelector('.cds-badge')?.textContent).toContain('Off');
    expect(rows[1].textContent).toContain('Patient chart opened');
  });
});
