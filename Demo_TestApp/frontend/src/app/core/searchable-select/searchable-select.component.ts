import { Component, ElementRef, HostListener, computed, inject, input, model, signal } from '@angular/core';

export interface SearchableOption {
  value: string;
  label: string;
  /** Secondary text shown (and searchable) next to the label, e.g. the vendor. */
  hint?: string;
}

/**
 * Small type-to-filter dropdown used where a plain <select> gets too long to scroll (workflows, EHR endpoints).
 * Shows the selected option's label when closed; typing filters by label and hint; arrows/Enter/Escape work.
 */
@Component({
  selector: 'app-searchable-select',
  standalone: true,
  template: `
    <div class="ss">
      <input
        type="text"
        class="ss__input"
        autocomplete="off"
        role="combobox"
        [attr.aria-expanded]="open()"
        [value]="open() ? query() : selectedLabel()"
        [placeholder]="open() ? 'Type to search…' : placeholder()"
        [disabled]="disabled()"
        (focus)="openList()"
        (click)="openList()"
        (input)="onType($any($event.target).value)"
        (keydown)="onKey($event)"
      />
      <span class="ss__caret" aria-hidden="true">▾</span>
      @if (open()) {
        <ul class="ss__list" role="listbox">
          @if (emptyLabel() !== null && !query().trim()) {
            <li class="ss__item" (mousedown)="choose('')">{{ emptyLabel() }}</li>
          }
          @for (o of filtered(); track o.value; let i = $index) {
            <li
              class="ss__item"
              role="option"
              [class.ss__item--active]="i === activeIndex()"
              [class.ss__item--selected]="o.value === value()"
              (mousedown)="choose(o.value)"
              (mouseenter)="activeIndex.set(i)"
            >
              {{ o.label }}@if (o.hint) { <span class="ss__hint">{{ o.hint }}</span> }
            </li>
          } @empty {
            <li class="ss__empty">No matches</li>
          }
        </ul>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .ss { position: relative; }
    .ss__input {
      width: 100%; height: 36px; padding: 0 28px 0 10px; box-sizing: border-box;
      border: 1px solid #d1d5db; border-radius: 6px; font-size: 13.5px; font-weight: 400; color: #1f2937; background: #fff;
    }
    .ss__caret { position: absolute; right: 10px; top: 50%; transform: translateY(-50%); color: #6b7280; pointer-events: none; font-size: 12px; }
    .ss__list {
      position: absolute; z-index: 20; left: 0; right: 0; top: calc(100% + 2px); margin: 0; padding: 4px 0; list-style: none;
      max-height: 240px; overflow-y: auto; background: #fff; border: 1px solid #d1d5db; border-radius: 6px;
      box-shadow: 0 6px 18px rgba(0, 0, 0, .12);
    }
    .ss__item { padding: 8px 10px; font-size: 13.5px; font-weight: 400; color: #1f2937; cursor: pointer; }
    .ss__item--active { background: #eef2ff; }
    .ss__item--selected { font-weight: 600; }
    .ss__hint { margin-left: 8px; font-size: 12px; color: #6b7280; }
    .ss__empty { padding: 8px 10px; font-size: 13px; color: #6b7280; }
  `],
})
export class SearchableSelectComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly options = input<SearchableOption[]>([]);
  readonly value = model<string>('');
  readonly placeholder = input('Select…');
  /** When set (even to ''), an extra first row with this label clears the selection. */
  readonly emptyLabel = input<string | null>(null);
  readonly disabled = input(false);

  protected readonly open = signal(false);
  protected readonly query = signal('');
  protected readonly activeIndex = signal(0);

  protected readonly selectedLabel = computed(() => {
    const current = this.value();
    if (!current) return this.emptyLabel() ?? '';
    return this.options().find(o => o.value === current)?.label ?? '';
  });

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return this.options();
    return this.options().filter(o => `${o.label} ${o.hint ?? ''}`.toLowerCase().includes(q));
  });

  protected openList(): void {
    if (this.disabled() || this.open()) return;
    this.query.set('');
    this.activeIndex.set(Math.max(0, this.options().findIndex(o => o.value === this.value())));
    this.open.set(true);
  }

  protected onType(text: string): void {
    this.query.set(text);
    this.activeIndex.set(0);
  }

  protected choose(value: string): void {
    this.value.set(value);
    this.open.set(false);
  }

  protected onKey(event: KeyboardEvent): void {
    const count = this.filtered().length;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.openList();
      this.activeIndex.set(Math.min(count - 1, this.activeIndex() + 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.activeIndex.set(Math.max(0, this.activeIndex() - 1));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const option = this.filtered()[this.activeIndex()];
      if (this.open() && option) this.choose(option.value);
    } else if (event.key === 'Escape' || event.key === 'Tab') {
      this.open.set(false);
    }
  }

  @HostListener('document:mousedown', ['$event'])
  protected onDocumentMouseDown(event: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) {
      this.open.set(false);
    }
  }
}
