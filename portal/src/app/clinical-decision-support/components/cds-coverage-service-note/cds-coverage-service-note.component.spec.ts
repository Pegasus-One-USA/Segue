import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CdsCoverageServiceNoteComponent } from './cds-coverage-service-note.component';

describe('CdsCoverageServiceNoteComponent', () => {
  function render(part: 'settings' | 'fixes') {
    TestBed.configureTestingModule({ imports: [CdsCoverageServiceNoteComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(CdsCoverageServiceNoteComponent);
    fixture.componentRef.setInput('part', part);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('points to the Coverage tab for payers and rules', () => {
    const el = render('settings');
    const link = el.querySelector('a') as HTMLAnchorElement;
    expect(link.textContent).toContain('Coverage (CRD)');
    expect(link.getAttribute('href')).toBe('/clinical-decision-support/coverage');
  });

  it('explains that fixes do not apply and orders are updated instead', () => {
    const el = render('fixes');
    expect(el.textContent).toContain("don't use one-click fixes");
    expect(el.textContent).toContain('ServiceRequest.Update (Unsigned Order)');
  });
});
