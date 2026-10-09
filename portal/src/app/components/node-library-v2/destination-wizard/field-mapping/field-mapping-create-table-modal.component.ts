import {
  AfterViewInit, ChangeDetectorRef, Component, ElementRef, HostListener, Injector, ViewChild, afterNextRender, computed,
  inject, input, output, signal,
} from '@angular/core';
import { addColumnDataTypesFor } from './field-mapping-add-column-modal.component';

export interface FmCreateTableColumnDraft {
  name: string;
  dataType: string;
}

export interface FmCreateTableSubmit {
  tableName: string;
  columns: FmCreateTableColumnDraft[];
  parentTable?: string;
  parentColumn?: string;
  foreignKeyColumnName?: string;
}

/**
 * Modal for the mapping canvas's "Create a new table…" affordance — table name, an optional
 * parent-table relationship (adds a real FK column), and a user-editable column list, all built with a
 * real CREATE TABLE against the destination (see FieldMappingCanvasComponent.submitCreateTable). Follows
 * the same dumb-presentational split as FieldMappingAddColumnModalComponent: this emits a submit payload,
 * the parent owns the HTTP call + submitting/error state.
 */
@Component({
  selector: 'app-field-mapping-create-table-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-create-table-modal.component.html',
  styleUrl: './field-mapping-create-table-modal.component.scss',
})
export class FieldMappingCreateTableModalComponent implements AfterViewInit {
  @ViewChild('nameInput') private readonly nameInput?: ElementRef<HTMLInputElement>;
  @ViewChild('parentTableSearchInput') private readonly parentTableSearchInput?: ElementRef<HTMLInputElement>;
  @ViewChild('parentPanel') private readonly parentPanel?: ElementRef<HTMLElement>;
  private readonly injector = inject(Injector);
  private readonly cdr = inject(ChangeDetectorRef);
  /** How many renders alignParentTablePanelWhenRendered waits for the panel before giving up (the menu then
   *  stays as it is — there is no panel on screen to correct). */
  private static readonly PANEL_ALIGN_ATTEMPTS = 10;

  /** Already-known table full names (e.g. "dbo.PatientContact") offered as a parent. */
  readonly existingTables = input<string[]>([]);
  /** Columns of an already-known table by full name — populates the "Relation column (parent)" dropdown. */
  readonly columnsForTable = input<(tableFullName: string) => string[]>(() => []);
  readonly submitting = input<boolean>(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<FmCreateTableSubmit>();
  readonly cancelled = output<void>();

  /** See FieldMappingAddColumnModalComponent.destType — same dialect-aware type list. */
  readonly destType = input<string>('sql');
  readonly dataTypes = computed(() => addColumnDataTypesFor(this.destType()));

  readonly tableName = signal('');
  readonly relation = signal<'standalone' | 'child'>('standalone');
  readonly parentTable = signal('');
  readonly parentColumn = signal('Id');
  readonly fkColumnName = signal('');
  // The fixed Id/bigint/PK row is always implied — this list is only the ADDITIONAL user columns.
  readonly columns = signal<FmCreateTableColumnDraft[]>([]);

  readonly parentColumnOptions = computed(() => {
    const table = this.parentTable();
    const cols = table ? this.columnsForTable()(table) : [];
    return cols.length ? cols : ['Id'];
  });

  /** A native <select>'s open option list can't be searched or kept from clipping against an
   *  ancestor's own overflow — .fm-createtable-dialog scrolls its own body (see its overflow-y: auto),
   *  which would clip a long table list badly. This custom panel renders position: fixed at
   *  coordinates measured from the trigger button's own getBoundingClientRect() instead — same
   *  technique as FieldMappingCanvasComponent's "+ Add a table…" panel. */
  readonly parentTableMenuOpen = signal(false);
  /** `aligned` is false until alignParentTablePanel has checked where the panel really landed — it stays
   *  hidden until then, so it never flashes at the wrong spot first. */
  readonly parentTableMenuStyle = signal<{ top?: number; bottom?: number; left: number; width: number; maxHeight: number; aligned?: boolean } | null>(null);
  readonly parentTableSearchQuery = signal('');

  readonly filteredExistingTables = computed(() => {
    const query = this.parentTableSearchQuery().trim().toLowerCase();
    const all = this.existingTables();
    return query ? all.filter(t => t.toLowerCase().includes(query)) : all;
  });

  toggleParentTableMenu(event: MouseEvent): void {
    if (this.parentTableMenuOpen()) {
      this.closeParentTableMenu();
      return;
    }

    const triggerRect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const margin = 8;
    const spaceBelow = window.innerHeight - triggerRect.bottom - margin;
    const spaceAbove = triggerRect.top - margin;
    const minUsableHeight = 120;

    this.parentTableMenuStyle.set(
      spaceBelow >= minUsableHeight || spaceBelow >= spaceAbove
        ? { top: triggerRect.bottom + 6, left: triggerRect.left, width: triggerRect.width, maxHeight: Math.max(minUsableHeight, spaceBelow) }
        : { bottom: window.innerHeight - triggerRect.top + 6, left: triggerRect.left, width: triggerRect.width, maxHeight: Math.max(minUsableHeight, spaceAbove) }
    );
    this.parentTableSearchQuery.set('');
    this.parentTableMenuOpen.set(true);
    this.alignParentTablePanelWhenRendered(0);
  }

  /** After the next render, so the panel exists; if it still doesn't, tries again on a later one (bounded). */
  private alignParentTablePanelWhenRendered(attempt: number): void {
    afterNextRender(() => {
      if (!this.parentTableMenuOpen()) return;
      if (this.parentPanel) {
        this.alignParentTablePanel(this.parentPanel.nativeElement);
        return;
      }
      if (attempt + 1 < FieldMappingCreateTableModalComponent.PANEL_ALIGN_ATTEMPTS) {
        setTimeout(() => this.alignParentTablePanelWhenRendered(attempt + 1), 16);
      }
    }, { injector: this.injector });
  }

  /**
   * The panel is position: fixed at viewport coordinates, but any ancestor with a transform, filter or
   * backdrop-filter becomes what "fixed" is measured from instead — and the Map fields screen sits inside
   * blurred overlays, which shifted the list right by the sidebar and down by the header. Moving the panel
   * out of this modal's own backdrop fixed one of those; this corrects for any of them: measure where the
   * panel actually is, and move it by the difference.
   */
  private alignParentTablePanel(panel: HTMLElement): void {
    const style = this.parentTableMenuStyle();
    if (!style) return;
    const rect = panel.getBoundingClientRect();
    const dx = rect.left - style.left;
    const dy = style.top !== undefined
      ? rect.top - style.top
      : rect.bottom - (window.innerHeight - (style.bottom ?? 0));
    const aligned = {
      ...style,
      left: style.left - dx,
      ...(style.top !== undefined ? { top: style.top - dy } : { bottom: (style.bottom ?? 0) + dy }),
      aligned: true,
    };
    // Marked aligned only now, with the corrected coordinates, and written only through the bindings.
    this.parentTableMenuStyle.set(aligned);
    // Render that before focusing: the panel is visibility: hidden until aligned, and a hidden element can't
    // take focus — the cursor stayed in Table name, so typing didn't search and Escape didn't close the list.
    this.cdr.detectChanges();
    this.parentTableSearchInput?.nativeElement.focus({ preventScroll: true });
  }

  /** The panel is fixed at coordinates taken when it opened; the dialog body now scrolls, which would leave it
   *  stranded away from its trigger — so it closes instead, like a native dropdown does. */
  onBodyScroll(): void {
    if (this.parentTableMenuOpen()) this.closeParentTableMenu();
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    if (this.parentTableMenuOpen()) this.closeParentTableMenu();
  }

  closeParentTableMenu(): void {
    this.parentTableMenuOpen.set(false);
    this.parentTableMenuStyle.set(null);
    this.parentTableSearchQuery.set('');
  }

  onParentTableSearchInput(value: string): void {
    this.parentTableSearchQuery.set(value);
  }

  clearParentTableSearch(): void {
    this.parentTableSearchQuery.set('');
    this.parentTableSearchInput?.nativeElement.focus();
  }

  selectParentTable(table: string): void {
    this.closeParentTableMenu();
    this.setParentTable(table);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClickForParentTableMenu(event: MouseEvent): void {
    // BOTH selectors, because the panel is rendered outside the backdrop (see the template's own note)
    // and so is no longer a descendant of the trigger's slot. Matching only the slot would close the
    // menu on the very first click inside it — typing in its search box, or picking an option.
    const target = event.target as HTMLElement;
    if (this.parentTableMenuOpen()
        && !target.closest('.fm-createtable-parent-slot')
        && !target.closest('.fm-createtable-parent-panel')) {
      this.closeParentTableMenu();
    }
  }

  ngAfterViewInit(): void {
    this.nameInput?.nativeElement.focus();
  }

  canSubmit(): boolean {
    if (this.submitting() || !this.tableName().trim()) return false;
    if (this.relation() === 'child' && (!this.parentTable() || !this.fkColumnName().trim())) return false;
    return this.columns().every(c => c.name.trim().length > 0);
  }

  onTableNameInput(value: string): void { this.tableName.set(value.replace(/\s/g, '')); }

  onRelationChange(value: 'standalone' | 'child'): void {
    this.relation.set(value);
    if (value === 'child' && !this.parentTable() && this.existingTables().length) {
      this.setParentTable(this.existingTables()[0]);
    }
  }

  /** Defaults the relation column / FK column name to match what the backend itself would default to
   *  ("Id" / "{ParentTableName}Id") when left untouched, so what's shown here matches what's created. */
  setParentTable(table: string): void {
    this.parentTable.set(table);
    this.parentColumn.set('Id');
    const bareName = table.includes('.') ? table.slice(table.indexOf('.') + 1) : table;
    this.fkColumnName.set(`${bareName}Id`);
  }

  onParentColumnChange(value: string): void { this.parentColumn.set(value); }
  onFkColumnNameInput(value: string): void { this.fkColumnName.set(value); }

  addColumn(): void {
    const types = this.dataTypes();
    this.columns.update(list => [...list, { name: '', dataType: types[2] ?? types[0] }]);
  }

  removeColumn(index: number): void {
    this.columns.update(list => list.filter((_, i) => i !== index));
  }

  onColumnNameInput(index: number, value: string): void {
    this.columns.update(list => list.map((c, i) => (i === index ? { ...c, name: value } : c)));
  }

  onColumnTypeChange(index: number, value: string): void {
    this.columns.update(list => list.map((c, i) => (i === index ? { ...c, dataType: value } : c)));
  }

  onBackdropClick(ev: MouseEvent): void {
    if (ev.target === ev.currentTarget && !this.submitting()) this.cancelled.emit();
  }

  onEscape(): void {
    // Kept even though the panel now binds its own Escape: the panel is rendered OUTSIDE the backdrop
    // (its blur made it a containing block and shifted the fixed-position list — see the template), so
    // an Escape from the panel no longer bubbles here at all. This still covers Escape pressed while
    // the panel is open but focus sits back in the dialog, which must close the panel rather than
    // cancel the WHOLE modal.
    if (this.parentTableMenuOpen()) {
      this.closeParentTableMenu();
      return;
    }
    if (!this.submitting()) this.cancelled.emit();
  }

  submit(): void {
    if (!this.canSubmit()) return;
    this.submitted.emit({
      tableName: this.tableName().trim(),
      columns: this.columns().map(c => ({ name: c.name.trim(), dataType: c.dataType })),
      ...(this.relation() === 'child'
        ? {
            parentTable: this.parentTable(),
            parentColumn: this.parentColumn(),
            foreignKeyColumnName: this.fkColumnName().trim(),
          }
        : {}),
    });
  }
}
