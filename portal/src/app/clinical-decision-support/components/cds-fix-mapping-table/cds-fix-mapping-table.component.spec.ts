import { TestBed } from '@angular/core/testing';
import { FormArray } from '@angular/forms';
import { CdsFixMappingTableComponent } from './cds-fix-mapping-table.component';
import { CdsFixCodeMapping, CdsFixTypeDescriptor } from '../../models/cds.models';
import { CDS_FIX_MAPPINGS_MAX, CdsMappingGroup, buildCdsFixesGroup } from '../../models/cds-service-options.util';
import { makeCreateFixType } from '../../testing/cds-test-data';

describe('CdsFixMappingTableComponent', () => {
  /** Built through the util, so the array carries the duplicate-trigger validator as on the page. */
  function render(descriptor: CdsFixTypeDescriptor = makeCreateFixType(), saved: CdsFixCodeMapping[] = []) {
    const mappings: FormArray<CdsMappingGroup> = buildCdsFixesGroup([descriptor], {
      enabled: true, fixTypes: [{ key: descriptor.key, enabled: true, mappings: saved }],
    }).controls.fixTypes.at(0).controls.mappings;
    TestBed.configureTestingModule({ imports: [CdsFixMappingTableComponent] });
    const fixture = TestBed.createComponent(CdsFixMappingTableComponent);
    fixture.componentRef.setInput('descriptor', descriptor);
    fixture.componentRef.setInput('mappings', mappings);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const add = () => {
      (el.querySelector('[data-testid="cds-fix-mapping-add"]') as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    const rowsEl = () => Array.from(el.querySelectorAll('[data-testid="cds-fix-mapping-row"]'));
    return { fixture, table: fixture.componentInstance, el, mappings, add, rowsEl };
  }

  it('draws the columns the fix type declares, with the code system as a pick list', () => {
    const { el, add, rowsEl } = render();
    add();

    const headers = Array.from(el.querySelectorAll('th')).map(h => (h.textContent ?? '').replace(/\s+/g, ' ').trim());
    expect(headers.slice(0, 4)).toEqual(['Drug class', 'Code system *', 'Code *', 'Name *']);
    const selects = rowsEl()[0].querySelectorAll('select');
    expect(selects.length).toBe(2);
    expect(Array.from(selects[1].options).map(o => o.textContent?.trim())).toEqual(['Epic preference list item', 'RxNorm']);
  });

  it('hides the trigger column when the fix has no trigger options', () => {
    const { el, add } = render(makeCreateFixType({ triggers: null }));
    add();
    expect(Array.from(el.querySelectorAll('th')).map(h => h.textContent?.trim())).not.toContain('Drug class');
  });

  it('starts each extra row on a trigger nobody mapped yet, and marks the array dirty', () => {
    const { add, mappings } = render();
    add();
    add();
    add();

    expect(mappings.getRawValue().map(m => m.trigger)).toEqual(['', 'nsaid', 'opioid']);
    expect(mappings.dirty).toBeTrue();
    expect(mappings.hasError('duplicateTrigger')).toBeFalse();
  });

  it('names a duplicated trigger by its label', () => {
    const { el, fixture, mappings } = render(makeCreateFixType(), [
      { trigger: 'nsaid', system: 's', code: '1', display: 'a' },
      { trigger: 'nsaid', system: 's', code: '2', display: 'b' },
    ]);
    fixture.detectChanges();
    expect(mappings.getError('duplicateTrigger')).toBe('nsaid');
    expect(el.textContent).toContain('Two rows are for NSAIDs. Keep one.');

    mappings.at(1).controls.trigger.setValue('');
    mappings.at(0).controls.trigger.setValue('');
    fixture.detectChanges();
    expect(el.textContent).toContain('Two rows are for any trigger. Keep one.');
  });

  it('shows row errors after a save attempt, and the "needs a code" message', () => {
    const { el, fixture, add } = render();
    add();
    expect(el.querySelector('.cell--error')).toBeNull();

    fixture.componentRef.setInput('showAllErrors', true);
    fixture.detectChanges();
    expect(el.querySelectorAll('.cell--error').length).toBe(2); // code and display are blank

    fixture.componentRef.setInput('needsMapping', true);
    fixture.detectChanges();
    expect(el.textContent).toContain("Add at least one code. This fix can't be offered without one.");
  });

  it('removes a row', () => {
    const { el, fixture, add, mappings } = render();
    add();
    add();
    (el.querySelector('[aria-label="Remove code row 1"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(mappings.length).toBe(1);
    expect(mappings.at(0).controls.trigger.value).toBe('nsaid');
  });

  it(`stops adding at ${CDS_FIX_MAPPINGS_MAX} rows`, () => {
    const d = makeCreateFixType({ triggers: null });
    const rows = Array.from({ length: CDS_FIX_MAPPINGS_MAX }, (_, i) => ({ trigger: `t${i}`, system: 's', code: `${i}`, display: 'x' }));
    const { el, table, mappings } = render(d, rows);
    expect(el.querySelector('[data-testid="cds-fix-mapping-add"]')).toBeNull();
    table.add();
    expect(mappings.length).toBe(CDS_FIX_MAPPINGS_MAX);
  });

  it('cannot add or remove while disabled', () => {
    const { el, table, mappings, fixture } = render(makeCreateFixType(), [{ system: 's', code: 'c', display: 'd' }]);
    mappings.disable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="cds-fix-mapping-add"]')).toBeNull();
    expect(el.querySelector('[aria-label="Remove code row 1"]')).toBeNull();
    table.add();
    table.remove(0);
    expect(mappings.length).toBe(1);
  });
});
