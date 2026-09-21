import { AfterViewInit, Component, ElementRef, ViewChild, computed, effect, input, output, signal, untracked } from '@angular/core';

export interface FmAddColumnSubmit {
  columnName: string;
  dataType: string;
}

/** Curated data types offered in the picker — a subset of the backend's full allowlist (see
 *  SqlDestinationSchemaService.ValidateDataType) covering the common cases without overwhelming choice. */
export const FM_ADD_COLUMN_DATA_TYPES: readonly string[] = [
  'nvarchar(50)', 'nvarchar(120)', 'nvarchar(255)', 'nvarchar(max)',
  'varchar(50)', 'varchar(255)',
  'int', 'bigint', 'bit',
  'date', 'datetime2', 'datetimeoffset',
  'decimal(18,2)',
  'uniqueidentifier',
];

/** A Fabric Warehouse supports a narrower T-SQL type set than SQL Server, and offering a type it rejects
 *  turns a working dialog into a failed ALTER TABLE against a live tenant. Mirrors
 *  FabricWarehouseDdlTypeValidator exactly: no nvarchar (varchar is UTF-8 collated there), no MAX lengths
 *  (8000 is the cap), and no uniqueidentifier. */
export const FM_ADD_COLUMN_DATA_TYPES_FABRIC: readonly string[] = [
  'varchar(50)', 'varchar(120)', 'varchar(255)', 'varchar(8000)',
  'int', 'bigint', 'smallint', 'bit',
  'date', 'datetime2',
  'decimal(18,2)',
];

/** The type list to offer for the given wizard destination type. */
export function addColumnDataTypesFor(destType: string): readonly string[] {
  return destType === 'fabricwarehouse' ? FM_ADD_COLUMN_DATA_TYPES_FABRIC : FM_ADD_COLUMN_DATA_TYPES;
}

/**
 * Modal for the mapping canvas's "+ Add column" affordance — column name + data type, submitting
 * triggers a real ALTER TABLE against the destination (see FieldMappingCanvasComponent.submitAddColumn).
 * Only shown while a live schema is known (hasSqlTables()); the parent renders this via a structural
 * @if so each open gets a fresh instance — no reset-on-reopen logic needed here.
 */
@Component({
  selector: 'app-field-mapping-add-column-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-add-column-modal.component.html',
  styleUrl: './field-mapping-add-column-modal.component.scss',
})
export class FieldMappingAddColumnModalComponent implements AfterViewInit {
  @ViewChild('nameInput') private readonly nameInput?: ElementRef<HTMLInputElement>;

  readonly tableName = input.required<string>();
  readonly submitting = input<boolean>(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<FmAddColumnSubmit>();
  readonly cancelled = output<void>();

  /** The wizard destination type — decides which data types are offered, since a Fabric Warehouse accepts a
   *  narrower set than SQL Server (see addColumnDataTypesFor). */
  readonly destType = input<string>('sql');
  readonly dataTypes = computed(() => addColumnDataTypesFor(this.destType()));
  readonly columnName = signal('');
  readonly dataType = signal<string>(FM_ADD_COLUMN_DATA_TYPES[2]);
  /** Keeps the selection inside the offered list when the destination type changes the list under it. */
  private readonly _pinDefault = effect(() => {
    const types = this.dataTypes();
    untracked(() => {
      if (!types.includes(this.dataType())) this.dataType.set(types[2] ?? types[0]);
    });
  });

  canSubmit(): boolean {
    return this.columnName().trim().length > 0 && !this.submitting();
  }

  ngAfterViewInit(): void {
    this.nameInput?.nativeElement.focus();
  }

  onColumnNameInput(value: string): void { this.columnName.set(value); }
  onDataTypeChange(value: string): void { this.dataType.set(value); }

  onBackdropClick(ev: MouseEvent): void {
    if (ev.target === ev.currentTarget && !this.submitting()) this.cancelled.emit();
  }

  onEscape(): void {
    if (!this.submitting()) this.cancelled.emit();
  }

  submit(): void {
    if (!this.canSubmit()) return;
    this.submitted.emit({ columnName: this.columnName().trim(), dataType: this.dataType() });
  }
}
