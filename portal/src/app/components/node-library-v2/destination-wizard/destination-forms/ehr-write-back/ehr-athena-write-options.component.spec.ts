import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { EhrAthenaWriteOptionsComponent } from './ehr-athena-write-options.component';

describe('EhrAthenaWriteOptionsComponent', () => {
  const render = () => {
    TestBed.configureTestingModule({ imports: [EhrAthenaWriteOptionsComponent] });
    const fixture = TestBed.createComponent(EhrAthenaWriteOptionsComponent);
    fixture.componentRef.setInput('providerId', new FormControl<string | null>(''));
    fixture.componentRef.setInput('departmentId', new FormControl<string | null>(''));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };

  // PR #244 review: athena gets every date taken from a dateTime in UTC (no practice time zone is configured), and
  // says so for each kind of record, not only lab results and notes.
  it('says every athena date and time is sent in UTC', () => {
    const text = render().querySelector('[data-testid="ewb-athena-utc-hint"]')?.textContent ?? '';
    expect(text).toContain('UTC');
    for (const kind of ['lab result', 'note', 'onset', 'medication start', 'vaccine']) {
      expect(text).toContain(kind);
    }
  });
});
