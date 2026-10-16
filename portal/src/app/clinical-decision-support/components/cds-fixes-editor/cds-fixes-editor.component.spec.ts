import { TestBed } from '@angular/core/testing';
import { CdsFixesEditorComponent } from './cds-fixes-editor.component';
import { CdsFixTypeDescriptor } from '../../models/cds.models';
import { CdsFixesGroup, buildCdsFixesGroup } from '../../models/cds-service-options.util';
import { makeCreateFixType, makeDeleteFixType } from '../../testing/cds-test-data';

describe('CdsFixesEditorComponent', () => {
  const DESCRIPTORS = [makeDeleteFixType(), makeCreateFixType()];

  function render(descriptors: CdsFixTypeDescriptor[] = DESCRIPTORS, group: CdsFixesGroup = buildCdsFixesGroup(descriptors, null)) {
    TestBed.configureTestingModule({ imports: [CdsFixesEditorComponent] });
    const fixture = TestBed.createComponent(CdsFixesEditorComponent);
    fixture.componentRef.setInput('descriptors', descriptors);
    fixture.componentRef.setInput('group', group);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const click = (sel: string) => {
      (el.querySelector(sel) as HTMLInputElement).click();
      fixture.detectChanges();
    };
    return { fixture, editor: fixture.componentInstance, el, group, click };
  }

  it('says so when the check offers no fixes', () => {
    const { el } = render([]);
    expect(el.textContent).toContain("This check doesn't offer one-click fixes.");
    expect(el.querySelector('[data-testid="cds-fixes-enabled"]')).toBeNull();
  });

  it('is off by default and hides the fix types until switched on', () => {
    const { el, click, group } = render();
    const master = el.querySelector('[data-testid="cds-fixes-enabled"]') as HTMLInputElement;

    expect(master.checked).toBeFalse();
    expect(el.querySelectorAll('[data-testid="cds-fix-type"]').length).toBe(0);

    click('[data-testid="cds-fixes-enabled"]');

    expect(group.controls.enabled.value).toBeTrue();
    const types = el.querySelectorAll('[data-testid="cds-fix-type"]');
    expect(types.length).toBe(2);
    expect(types[0].textContent).toContain('Remove the draft order');
    expect((el.querySelector('#cds-fix-remove-draft') as HTMLInputElement).checked).toBeFalse();
  });

  it('shows the mapping table only for a switched-on type that takes codes', () => {
    const { el, click } = render();
    click('[data-testid="cds-fixes-enabled"]');
    click('#cds-fix-remove-draft');
    expect(el.querySelector('app-cds-fix-mapping-table')).toBeNull();

    click('#cds-fix-add-alternative');
    expect(el.querySelectorAll('app-cds-fix-mapping-table').length).toBe(1);
    expect(el.textContent).toContain("Add at least one code. This fix can't be offered without one.");
  });

  it('offers only switched-on types as the pre-selected fix, and clears it when its type goes off', () => {
    const { el, click, group } = render();
    click('[data-testid="cds-fixes-enabled"]');
    click('#cds-fix-remove-draft');

    const options = () => Array.from((el.querySelector('#cds-fix-recommended') as HTMLSelectElement).options).map(o => o.value);
    expect(options()).toEqual(['', 'remove-draft']);

    group.controls.recommendedFixType.setValue('remove-draft');
    click('#cds-fix-remove-draft');

    expect(group.controls.recommendedFixType.value).toBe('');
    expect(options()).toEqual(['']);
  });

  it('asks for at least one fix once a save was tried with fixes on and none ticked', () => {
    const { el, click, fixture } = render();
    click('[data-testid="cds-fixes-enabled"]');
    expect(el.querySelector('[data-testid="cds-fixes-none-on"]')).toBeNull();

    fixture.componentRef.setInput('showAllErrors', true);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="cds-fixes-none-on"]')?.textContent).toContain('Tick at least one fix below');

    click('#cds-fix-remove-draft');
    expect(el.querySelector('[data-testid="cds-fixes-none-on"]')).toBeNull();
  });

  it('is read-only when the group is disabled', () => {
    const group = buildCdsFixesGroup(DESCRIPTORS, { enabled: true, fixTypes: [] });
    group.disable();
    const { el } = render(DESCRIPTORS, group);
    expect((el.querySelector('[data-testid="cds-fixes-enabled"]') as HTMLInputElement).disabled).toBeTrue();
    expect((el.querySelector('#cds-fix-remove-draft') as HTMLInputElement).disabled).toBeTrue();
  });
});
