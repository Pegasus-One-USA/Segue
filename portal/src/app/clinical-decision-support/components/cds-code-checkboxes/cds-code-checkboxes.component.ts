import { Component, effect, input, signal } from '@angular/core';
import { FormControl } from '@angular/forms';

export interface CdsCodeOption {
  value: string;
  label: string;
}

/**
 * A group of checkboxes that writes the ticked values (in option order) to a string[] control, e.g. a coverage
 * rule's documentation purposes. Follows the control's value and disabled state.
 */
@Component({
  selector: 'app-cds-code-checkboxes',
  standalone: true,
  imports: [],
  templateUrl: './cds-code-checkboxes.component.html',
  styleUrl: './cds-code-checkboxes.component.scss',
})
export class CdsCodeCheckboxesComponent {
  readonly control = input.required<FormControl<string[]>>();
  readonly options = input.required<CdsCodeOption[]>();
  /** Prefix for the checkbox ids (must be unique on the page). */
  readonly idPrefix = input.required<string>();
  /** The group's accessible name. */
  readonly label = input.required<string>();

  readonly selected = signal<string[]>([]);
  readonly disabled = signal(false);

  constructor() {
    effect(onCleanup => {
      const control = this.control();
      const sync = () => {
        this.selected.set([...(control.value ?? [])]);
        this.disabled.set(control.disabled);
      };
      sync();
      const valueSub = control.valueChanges.subscribe(sync);
      const statusSub = control.statusChanges.subscribe(sync);
      onCleanup(() => {
        valueSub.unsubscribe();
        statusSub.unsubscribe();
      });
    });
  }

  isChecked(value: string): boolean {
    return this.selected().includes(value);
  }

  toggle(value: string, checked: boolean): void {
    const current = new Set(this.selected());
    if (checked) current.add(value);
    else current.delete(value);
    const next = this.options().map(o => o.value).filter(v => current.has(v));
    const control = this.control();
    control.setValue(next);
    control.markAsDirty();
    control.markAsTouched();
  }
}
