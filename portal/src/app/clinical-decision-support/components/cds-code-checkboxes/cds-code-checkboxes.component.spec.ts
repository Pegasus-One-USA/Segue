import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { CdsCodeCheckboxesComponent } from './cds-code-checkboxes.component';

describe('CdsCodeCheckboxesComponent', () => {
  const options = [
    { value: 'withpa', label: 'With the prior authorization request' },
    { value: 'withclaim', label: 'With the claim' },
    { value: 'withorder', label: 'With the order' },
  ];

  function create(initial: string[] = []) {
    TestBed.configureTestingModule({ imports: [CdsCodeCheckboxesComponent] });
    const control = new FormControl<string[]>(initial, { nonNullable: true });
    const fixture = TestBed.createComponent(CdsCodeCheckboxesComponent);
    fixture.componentRef.setInput('control', control);
    fixture.componentRef.setInput('options', options);
    fixture.componentRef.setInput('idPrefix', 'purpose');
    fixture.componentRef.setInput('label', 'Purposes');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const box = (value: string) => el.querySelector(`#purpose-${value}`) as HTMLInputElement;
    return { fixture, control, box };
  }

  it('ticks the current values', () => {
    const { box } = create(['withclaim']);
    expect(box('withclaim').checked).toBeTrue();
    expect(box('withpa').checked).toBeFalse();
  });

  it('writes ticked values in option order and marks the control dirty', () => {
    const { box, control } = create(['withorder']);
    box('withpa').click();
    expect(control.value).toEqual(['withpa', 'withorder']);
    expect(control.dirty).toBeTrue();
    box('withorder').click();
    expect(control.value).toEqual(['withpa']);
  });

  it('follows a value set from outside and the disabled state', () => {
    const { box, control, fixture } = create();
    control.setValue(['withclaim']);
    control.disable();
    fixture.detectChanges();
    expect(box('withclaim').checked).toBeTrue();
    expect(box('withclaim').disabled).toBeTrue();
  });
});
