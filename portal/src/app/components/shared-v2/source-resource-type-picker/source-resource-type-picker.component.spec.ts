import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SourceResourceTypePickerComponent } from './source-resource-type-picker.component';

describe('SourceResourceTypePickerComponent', () => {
  let fixture: ComponentFixture<SourceResourceTypePickerComponent>;
  let emitted: string[][];

  const render = (inputs: { available?: string[]; selected?: string[]; showError?: boolean; disabled?: boolean }) => {
    fixture.componentRef.setInput('available', inputs.available ?? ['Patient', 'Condition', 'Observation']);
    fixture.componentRef.setInput('selected', inputs.selected ?? []);
    fixture.componentRef.setInput('showError', inputs.showError ?? false);
    fixture.componentRef.setInput('disabled', inputs.disabled ?? false);
    fixture.detectChanges();
  };
  const el = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const names = () => Array.from(el().querySelectorAll('.srtp-name')).map((n) => n.textContent?.trim());
  const button = (label: string) =>
    Array.from(el().querySelectorAll<HTMLButtonElement>('.srtp-action')).find((b) => b.textContent?.includes(label))!;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SourceResourceTypePickerComponent] });
    fixture = TestBed.createComponent(SourceResourceTypePickerComponent);
    emitted = [];
    fixture.componentInstance.selectionChange.subscribe((types) => emitted.push(types));
  });

  it('offers only the available types, and keeps a saved type outside them so it can be removed', () => {
    render({ selected: ['Task'] });
    expect(names()).toEqual(['Patient', 'Condition', 'Observation', 'Task']);
    const task = el().querySelectorAll<HTMLInputElement>('.srtp-item input')[3];
    expect(task.checked).toBeTrue();
    expect(task.disabled).toBeFalse();
  });

  it('ticks a type in display order', () => {
    render({ selected: ['Observation'] });
    el().querySelectorAll<HTMLInputElement>('.srtp-item input')[0].click();
    expect(emitted).toEqual([['Patient', 'Observation']]);
  });

  it('selects every visible type, respecting the search, and clears', () => {
    render({});
    const input = el().querySelector<HTMLInputElement>('.srtp-search-input')!;
    input.value = 'ob';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(names()).toEqual(['Observation']);

    button('Select all').click();
    expect(emitted.pop()).toEqual(['Observation']);

    render({ selected: ['Patient'] });
    button('Clear').click();
    expect(emitted.pop()).toEqual([]);
  });

  it('shows the required error only once the host asks for it', () => {
    render({});
    expect(el().querySelector('.srtp-error')).toBeNull();
    render({ showError: true });
    expect(el().querySelector('.srtp-error')?.textContent).toContain('Choose at least one resource type');
    render({ showError: true, selected: ['Patient'] });
    expect(el().querySelector('.srtp-error')).toBeNull();
  });

  it('emits nothing while disabled', () => {
    render({ disabled: true });
    button('Select all').click();
    expect(emitted).toEqual([]);
  });
});
