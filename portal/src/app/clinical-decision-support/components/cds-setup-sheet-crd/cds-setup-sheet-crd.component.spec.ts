import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CdsSetupSheetCrdComponent } from './cds-setup-sheet-crd.component';
import { CdsSetupSheetCrd } from '../../models/cds.models';
import { makeSetupSheetCrd } from '../../testing/cds-test-data';

describe('CdsSetupSheetCrdComponent', () => {
  function render(crd: CdsSetupSheetCrd) {
    TestBed.configureTestingModule({ imports: [CdsSetupSheetCrdComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(CdsSetupSheetCrdComponent);
    fixture.componentRef.setInput('crd', crd);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the hook, the Epic APIs with the order update API explained, and the Segue key set', () => {
    const el = render(makeSetupSheetCrd());
    expect(el.textContent).toContain('order-sign');
    const apis = el.querySelector('[data-testid="cds-setup-sheet-crd-apis"]')!;
    expect(apis.textContent).toContain('ServiceRequest.Update (Unsigned Order)');
    expect(apis.textContent).toContain('lets Segue add coverage information');
    expect(el.querySelector('[data-testid="cds-setup-sheet-crd-jwks"]')?.textContent)
      .toContain('https://segue.example.org/cds-services/.well-known/jwks.json');
    expect(el.textContent).toContain('Segue adds the coverage information to the order itself');
  });

  it('lists each payer with how it is recognised and answered, and the notes', () => {
    const el = render(makeSetupSheetCrd());
    const rows = el.querySelectorAll('[data-testid="cds-setup-sheet-crd-payer"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('ACME01');
    expect(rows[0].textContent).toContain('Every plan');
    expect(rows[0].textContent).toContain('Rules in Segue');
    expect(rows[1].textContent).toContain("Payer's service");
    expect(rows[1].textContent).toContain("receives the EHR's access token");
    expect(rows[1].textContent).toContain('(off)');
    expect(el.textContent).toContain('check Segue\'s signature');
    expect(el.querySelector('[data-testid="cds-setup-sheet-crd-notes"]')?.textContent).toContain('CRD Request');
  });

  it('points to the Coverage tab when there are no payers, and shows the DTR app to allow-list', () => {
    const el = render(makeSetupSheetCrd({ payers: [], useSystemActions: false, dtrAppUrl: 'https://dtr.example.org/launch' }));
    expect(el.querySelector('[data-testid="cds-setup-sheet-crd-no-payers"]')?.textContent).toContain('Coverage (CRD)');
    expect(el.textContent).toContain('the clinician sees cards only');
    expect(el.textContent).toContain('https://dtr.example.org/launch');
    expect(el.textContent).toContain('SMART app allow-list');
  });
});
