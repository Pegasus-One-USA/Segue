import { TestBed } from '@angular/core/testing';
import { CdsSettingsFormComponent } from './cds-settings-form.component';
import { CdsSettingDescriptor } from '../../models/cds.models';
import { CdsSettingsGroup, buildCdsSettingsGroup } from '../../models/cds-settings.util';
import { makeSetting } from '../../testing/cds-test-data';

/** The settings form is built only from the handler's descriptor: one field per entry, of the matching kind. */
describe('CdsSettingsFormComponent', () => {
  const DESCRIPTORS: CdsSettingDescriptor[] = [
    makeSetting({ key: 'egfr', label: 'eGFR threshold', type: 'number', defaultValue: 30, min: 5, max: 120, help: 'mL/min.' }),
    makeSetting({ key: 'level', label: 'Card level', type: 'select', defaultValue: 'warning', options: [
      { value: 'info', label: 'Info' }, { value: 'warning', label: 'Warning' },
    ] }),
    makeSetting({ key: 'strict', label: 'Strict mode', type: 'boolean', defaultValue: false }),
    makeSetting({ key: 'codes', label: 'Codes needing prior auth', type: 'list', defaultValue: ['70551'] }),
    makeSetting({ key: 'note', label: 'Note', type: 'text', defaultValue: null, required: true }),
  ];

  function create(descriptors = DESCRIPTORS, group?: CdsSettingsGroup) {
    TestBed.configureTestingModule({ imports: [CdsSettingsFormComponent] });
    const fixture = TestBed.createComponent(CdsSettingsFormComponent);
    const g = group ?? buildCdsSettingsGroup(descriptors, null);
    fixture.componentRef.setInput('descriptors', descriptors);
    fixture.componentRef.setInput('group', g);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, group: g, field: (key: string) => el.querySelector(`#cds-setting-${key}`) as HTMLInputElement };
  }

  it('renders one control of the right kind per descriptor', () => {
    const { el, field } = create();

    expect(field('egfr').type).toBe('number');
    expect(field('egfr').getAttribute('min')).toBe('5');
    expect(field('level').tagName).toBe('SELECT');
    expect(field('strict').type).toBe('checkbox');
    expect(field('codes').tagName).toBe('TEXTAREA');
    expect(field('note').type).toBe('text');
    expect(el.querySelectorAll('.cds-field').length).toBe(5);
  });

  it('shows the stored or default values', () => {
    const { field } = create();

    expect(field('egfr').value).toBe('30');
    expect((field('level') as unknown as HTMLSelectElement).value).toBe('warning');
    expect(field('strict').checked).toBeFalse();
    expect((field('codes') as unknown as HTMLTextAreaElement).value).toBe('70551');
  });

  it('offers "Use default" only on a select that is not required', () => {
    const { field } = create();
    const options = Array.from((field('level') as unknown as HTMLSelectElement).options).map(o => o.textContent?.trim());
    expect(options).toEqual(['Use default', 'Info', 'Warning']);
  });

  it('shows help, range and default hints', () => {
    const { el } = create();
    const help = el.querySelectorAll('.cds-field__help')[0].textContent ?? '';
    expect(help).toContain('mL/min.');
    expect(help).toContain('Between 5 and 120.');
    expect(help).toContain('Default: 30.');
    expect(el.textContent).toContain('Default: Warning.');
  });

  it('shows no default hint for an empty text default', () => {
    const { el } = create([makeSetting({ key: 'payer', label: 'Payer name', type: 'text', defaultValue: '', help: 'Optional.' })]);

    expect(el.textContent).toContain('Optional.');
    expect(el.textContent).not.toContain('Default:');
  });

  it('writes user input into the group', () => {
    const { fixture, field, group } = create();
    const input = field('egfr');
    input.value = '50';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(group.controls['egfr'].value).toBe(50);
    expect(group.dirty).toBeTrue();
  });

  it('shows errors only once touched, or for every field after a failed save', () => {
    const { fixture, el } = create();
    expect(el.querySelector('.cds-field__error')).toBeNull();

    fixture.componentRef.setInput('showAllErrors', true);
    fixture.detectChanges();

    expect(el.querySelector('.cds-field__error')?.textContent).toContain('Note is required.');
  });

  it('says so when the check has no settings', () => {
    const { el } = create([]);
    expect(el.textContent).toContain('This check has no settings.');
  });
});
