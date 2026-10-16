import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { CdsFetchMissingDataComponent } from './cds-fetch-missing-data.component';

describe('CdsFetchMissingDataComponent', () => {
  function render(control: FormControl<boolean>) {
    TestBed.configureTestingModule({ imports: [CdsFetchMissingDataComponent] });
    const fixture = TestBed.createComponent(CdsFetchMissingDataComponent);
    fixture.componentRef.setInput('control', control);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, box: () => el.querySelector('[data-testid="cds-fetch-missing"]') as HTMLInputElement };
  }

  it('reflects and changes the control, and says nothing fetched is stored', () => {
    const control = new FormControl(true, { nonNullable: true });
    const { el, box, fixture } = render(control);

    expect(box().checked).toBeTrue();
    expect(el.textContent).toContain('Nothing fetched is stored.');

    box().click();
    fixture.detectChanges();
    expect(control.value).toBeFalse();
    expect(control.dirty).toBeTrue();
  });

  it('is disabled with the control', () => {
    const control = new FormControl(false, { nonNullable: true });
    control.disable();
    const { box } = render(control);
    expect(box().checked).toBeFalse();
    expect(box().disabled).toBeTrue();
  });
});
