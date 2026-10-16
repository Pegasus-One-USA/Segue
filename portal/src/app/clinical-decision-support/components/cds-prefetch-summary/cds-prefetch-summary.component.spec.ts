import { TestBed } from '@angular/core/testing';
import { CdsPrefetchSummaryComponent } from './cds-prefetch-summary.component';

describe('CdsPrefetchSummaryComponent', () => {
  function render(prefetch: Record<string, string>): HTMLElement {
    TestBed.configureTestingModule({ imports: [CdsPrefetchSummaryComponent] });
    const fixture = TestBed.createComponent(CdsPrefetchSummaryComponent);
    fixture.componentRef.setInput('prefetch', prefetch);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists each prefetch key with its query', () => {
    const el = render({ patient: 'Patient/{{context.patientId}}', allergies: 'AllergyIntolerance?patient={{context.patientId}}' });

    expect(Array.from(el.querySelectorAll('dt')).map(d => d.textContent)).toEqual(['patient', 'allergies']);
    expect(el.querySelectorAll('dd')[1].textContent).toContain('AllergyIntolerance?patient=');
  });

  it('says only context is used when there is no prefetch', () => {
    expect(render({}).textContent).toContain('only uses the details the EHR sends');
  });
});
