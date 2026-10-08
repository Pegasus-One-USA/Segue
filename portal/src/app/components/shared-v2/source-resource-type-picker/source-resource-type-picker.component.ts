import { Component, computed, input, output, signal } from '@angular/core';

/** One row of the picker: a type the vendor can read, or a saved type it no longer offers (kept so it can be
 *  unticked rather than silently dropped). */
interface PickerRow {
  type: string;
  offered: boolean;
}

/**
 * "Resource types to read" for a source node. The source declares what it reads; destinations then choose only
 * from these types, and the fetch never widens past them. Offers only the types the vendor can read
 * (`available`); a saved selection outside that list still shows, ticked, so it can be removed. At least one type
 * is required: `showError` turns the empty state into an error once the host has tried to save.
 */
@Component({
  selector: 'app-source-resource-type-picker',
  standalone: true,
  templateUrl: './source-resource-type-picker.component.html',
  styleUrl: './source-resource-type-picker.component.scss',
})
export class SourceResourceTypePickerComponent {
  /** Types the vendor can read, in display order. */
  readonly available = input<string[]>([]);
  readonly selected = input<string[]>([]);
  readonly disabled = input<boolean>(false);
  /** Show the "choose at least one" error when nothing is selected (the host sets it after a refused save). */
  readonly showError = input<boolean>(false);
  readonly selectionChange = output<string[]>();

  protected readonly query = signal('');

  private readonly rows = computed<PickerRow[]>(() => {
    const available = this.available();
    const offered = new Set(available);
    const extra = this.selected().filter((t) => !offered.has(t));
    return [
      ...available.map((type) => ({ type, offered: true })),
      ...extra.map((type) => ({ type, offered: false })),
    ];
  });

  protected readonly visibleRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.rows().filter((r) => r.type.toLowerCase().includes(q)) : this.rows();
  });

  protected readonly selectedCount = computed(() => this.selected().length);

  /** Every visible, offered type is already ticked — "Select all" has nothing left to add. */
  protected readonly allVisibleSelected = computed(() => {
    const selected = new Set(this.selected());
    const offered = this.visibleRows().filter((r) => r.offered);
    return offered.length > 0 && offered.every((r) => selected.has(r.type));
  });

  protected isSelected(type: string): boolean {
    return this.selected().includes(type);
  }

  protected onQueryInput(value: string): void {
    this.query.set(value);
  }

  protected clearQuery(): void {
    this.query.set('');
  }

  protected toggle(type: string): void {
    if (this.disabled()) return;
    const current = this.selected();
    this.selectionChange.emit(
      current.includes(type) ? current.filter((t) => t !== type) : this.inDisplayOrder([...current, type]),
    );
  }

  /** Ticks every visible offered type (respects the search), keeping whatever else was already ticked. */
  protected selectAll(): void {
    if (this.disabled()) return;
    const next = new Set(this.selected());
    this.visibleRows().filter((r) => r.offered).forEach((r) => next.add(r.type));
    this.selectionChange.emit(this.inDisplayOrder([...next]));
  }

  protected clear(): void {
    if (this.disabled()) return;
    this.selectionChange.emit([]);
  }

  private inDisplayOrder(types: string[]): string[] {
    const order = this.rows().map((r) => r.type);
    return [...types].sort((a, b) => order.indexOf(a) - order.indexOf(b));
  }
}
