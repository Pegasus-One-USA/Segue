import { Component, computed, effect, input, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';

/** What separates pasted values. Never , or ; (they occur in payer names). */
const SEPARATORS = /[\r\n\t]/;

/**
 * A list of short text values as removable chips (payer identifiers, plan values). Type a value and press Enter (or
 * leave the box) to add it. Commas and semicolons are part of a value, because payer names often contain them
 * ("Aetna Life Insurance Company, Inc."); a pasted list is split on line breaks and tabs only. Duplicates (any case)
 * are ignored. Writes a string[] to `control`.
 */
@Component({
  selector: 'app-cds-chip-list-input',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-chip-list-input.component.html',
  styleUrl: './cds-chip-list-input.component.scss',
})
export class CdsChipListInputComponent {
  readonly control = input.required<FormControl<string[]>>();
  readonly inputId = input.required<string>();
  readonly placeholder = input('');
  readonly maxItems = input(50);
  readonly maxLength = input(200);
  /** Mark the field as invalid (red border). */
  readonly invalid = input(false);

  readonly draft = signal('');
  readonly tooLong = signal(false);
  readonly values = signal<string[]>([]);
  readonly disabled = signal(false);

  readonly full = computed(() => this.values().length >= this.maxItems());

  constructor() {
    // Mirrors the control (its value can also be set from outside, e.g. a form reset).
    effect(onCleanup => {
      const control = this.control();
      const sync = () => {
        this.values.set([...(control.value ?? [])]);
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

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.commit();
    } else if (event.key === 'Backspace' && !this.draft()) {
      const list = this.values();
      if (list.length) this.remove(list.length - 1);
    }
  }

  onInput(value: string): void {
    this.draft.set(value);
    if (SEPARATORS.test(value)) {
      this.commit();
      return;
    }
    this.tooLong.set(value.trim().length > this.maxLength());
  }

  /** Adds what was typed (split on line breaks and tabs). Values over the length limit stay in the box with a message. */
  commit(): void {
    const parts = this.draft().split(SEPARATORS).map(p => p.trim()).filter(p => p.length > 0);
    if (parts.length === 0) {
      this.draft.set('');
      this.tooLong.set(false);
      return;
    }
    const next = [...this.values()];
    const left: string[] = [];
    for (const part of parts) {
      if (part.length > this.maxLength() || next.length >= this.maxItems()) {
        left.push(part);
        continue;
      }
      if (!next.some(v => v.toLowerCase() === part.toLowerCase())) next.push(part);
    }
    if (next.length !== this.values().length) this.write(next);
    this.draft.set(left.join(' '));
    this.tooLong.set(left.some(p => p.length > this.maxLength()));
  }

  remove(index: number): void {
    const next = [...this.values()];
    next.splice(index, 1);
    this.write(next);
  }

  private write(next: string[]): void {
    const control = this.control();
    control.setValue(next);
    control.markAsDirty();
    control.markAsTouched();
  }
}
