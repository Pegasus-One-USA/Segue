import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { CdsChipListInputComponent } from './cds-chip-list-input.component';

describe('CdsChipListInputComponent', () => {
  function create(initial: string[] = [], maxItems = 50, maxLength = 10) {
    TestBed.configureTestingModule({ imports: [CdsChipListInputComponent] });
    const control = new FormControl<string[]>(initial, { nonNullable: true });
    const fixture = TestBed.createComponent(CdsChipListInputComponent);
    fixture.componentRef.setInput('control', control);
    fixture.componentRef.setInput('inputId', 'chips');
    fixture.componentRef.setInput('maxItems', maxItems);
    fixture.componentRef.setInput('maxLength', maxLength);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const chips = () => Array.from(el.querySelectorAll('.chip__text')).map(c => c.textContent?.trim());
    return { fixture, cmp: fixture.componentInstance, control, el, chips };
  }

  it('shows the current values as chips', () => {
    expect(create(['AET01', 'Acme']).chips()).toEqual(['AET01', 'Acme']);
  });

  it('adds a typed value on Enter, ignoring a duplicate in any case', () => {
    const { cmp, control, fixture, chips } = create(['Acme']);
    cmp.onInput('AET01');
    cmp.onKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    cmp.onInput('acme');
    cmp.commit();
    fixture.detectChanges();
    expect(control.value).toEqual(['Acme', 'AET01']);
    expect(control.dirty).toBeTrue();
    expect(chips()).toEqual(['Acme', 'AET01']);
    expect(cmp.draft()).toBe('');
  });

  it('keeps commas and semicolons inside a value (payer names have them) and splits a paste on line breaks', () => {
    const { cmp, control } = create([], 50, 200);
    cmp.onInput('Aetna Life Insurance Company, Inc.');
    expect(control.value).toEqual([]);
    cmp.onKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(control.value).toEqual(['Aetna Life Insurance Company, Inc.']);

    cmp.onInput('a; b\nc\td');
    expect(control.value).toEqual(['Aetna Life Insurance Company, Inc.', 'a; b', 'c', 'd']);
  });

  it('keeps a value that is too long in the box with a message', () => {
    const { cmp, control, fixture, el } = create();
    cmp.onInput('much-too-long-value');
    cmp.commit();
    fixture.detectChanges();
    expect(control.value).toEqual([]);
    expect(cmp.draft()).toBe('much-too-long-value');
    expect(el.textContent).toContain('10 characters or fewer');
  });

  it('stops adding at the limit', () => {
    const { cmp, control, fixture, el } = create(['a'], 2);
    cmp.onInput('b\nc');
    fixture.detectChanges();
    expect(control.value).toEqual(['a', 'b']);
    expect(el.textContent).toContain('most allowed (2)');
  });

  it('removes a chip with its button and the last one with Backspace on an empty box', () => {
    const { el, control, fixture, cmp } = create(['a', 'b', 'c']);
    (el.querySelector('.chip__remove') as HTMLButtonElement).click();
    expect(control.value).toEqual(['b', 'c']);
    cmp.onKeydown(new KeyboardEvent('keydown', { key: 'Backspace' }));
    fixture.detectChanges();
    expect(control.value).toEqual(['b']);
  });

  it('follows a value set from outside and hides editing when disabled', () => {
    const { control, fixture, chips, el } = create(['a']);
    control.setValue(['x', 'y']);
    control.disable();
    fixture.detectChanges();
    expect(chips()).toEqual(['x', 'y']);
    expect(el.querySelector('input')).toBeNull();
    expect(el.querySelector('.chip__remove')).toBeNull();
  });
});
