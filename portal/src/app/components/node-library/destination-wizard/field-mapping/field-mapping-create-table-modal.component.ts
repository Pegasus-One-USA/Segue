import { AfterViewInit, Component, ElementRef, ViewChild, computed, input, output, signal } from '@angular/core';
import { FM_ADD_COLUMN_DATA_TYPES } from './field-mapping-add-column-modal.component';

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

  /** Already-known table full names (e.g. "dbo.PatientContact") offered as a parent. */
  readonly existingTables = input<string[]>([]);
  /** Columns of an already-known table by full name — populates the "Relation column (parent)" dropdown. */
  readonly columnsForTable = input<(tableFullName: string) => string[]>(() => []);
  readonly submitting = input<boolean>(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<FmCreateTableSubmit>();
  readonly cancelled = output<void>();

  readonly dataTypes = FM_ADD_COLUMN_DATA_TYPES;

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

  ngAfterViewInit(): void {
    this.nameInput?.nativeElement.focus();
  }

  canSubmit(): boolean {
    if (this.submitting() || !this.tableName().trim()) return false;
    if (this.relation() === 'child' && (!this.parentTable() || !this.fkColumnName().trim())) return false;
    return this.columns().every(c => c.name.trim().length > 0);
  }

  onTableNameInput(value: string): void { this.tableName.set(value); }

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
    this.columns.update(list => [...list, { name: '', dataType: this.dataTypes[2] }]);
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
