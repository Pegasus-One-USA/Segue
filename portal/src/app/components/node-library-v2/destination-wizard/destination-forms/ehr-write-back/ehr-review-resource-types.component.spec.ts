import { TestBed } from '@angular/core/testing';
import { EhrReviewResourceTypesComponent } from './ehr-review-resource-types.component';

describe('EhrReviewResourceTypesComponent', () => {
  const render = (lines: { resourceType: string; kinds: string[] }[]) => {
    TestBed.configureTestingModule({ imports: [EhrReviewResourceTypesComponent] });
    const fixture = TestBed.createComponent(EhrReviewResourceTypesComponent);
    fixture.componentRef.setInput('lines', lines);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };

  it('lists each type with the kinds written, in plain words', () => {
    const el = render([
      { resourceType: 'AllergyIntolerance', kinds: [] },
      { resourceType: 'Observation', kinds: ['Vital signs', 'Lines, drains and airways'] },
    ]);
    expect(Array.from(el.querySelectorAll('[data-testid="ewb-review-type"]')).map((l) => l.textContent!.trim()))
      .toEqual(['AllergyIntolerance', 'Observation: Vital signs, Lines, drains and airways']);
  });

  it('shows a dash when nothing is selected', () => {
    expect(render([]).textContent!.trim()).toBe('—');
  });
});
