import { AfterViewInit, Component, ElementRef, ViewChild, computed, effect, input, output, signal, untracked } from '@angular/core';

export interface FmAddColumnSubmit {
  columnName: string;
  dataType: string;
}

/** Curated data types offered in the picker for a SQL Server destination — a subset of the backend's full
 *  allowlist (see SqlServerDdlTypeValidator) covering the common cases without overwhelming choice, in SQL
 *  Server's own native spelling. */
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

/** Same curated shape as FM_ADD_COLUMN_DATA_TYPES, but in MySQL's own native spelling (MySqlDdlTypeValidator)
 *  instead of SQL Server's — a SQL-Server-only keyword either has no MySQL equivalent at all
 *  ("uniqueidentifier"/"datetimeoffset" — MySqlDdlTypeValidator has no mapping for either) or only happens to
 *  work because the validator translates it ("nvarchar"→"varchar", "datetime2"→"datetime"). Offering the real
 *  names instead avoids a picker that's silently wrong for the database the author is actually looking at:
 *  "timestamp" is MySQL's own closest timezone-aware type (stored as UTC, converted per the session's
 *  time_zone on read/write — unlike "datetime", which is timezone-oblivious), and "char(36)" is what an
 *  offered UUID value is actually stored as (MySQL has no native UUID type).
 */
export const FM_ADD_COLUMN_DATA_TYPES_MYSQL: readonly string[] = [
  'varchar(50)', 'varchar(120)', 'varchar(255)', 'text',
  'int', 'bigint', 'bit',
  'date', 'datetime', 'timestamp',
  'decimal(18,2)',
  'char(36)',
];

/** Same curated shape again, in PostgreSQL's own native spelling (PostgreSqlDdlTypeValidator). "boolean" and
 *  "uuid" are PostgreSQL's real types for what SQL Server spells "bit"/"uniqueidentifier" (both still accepted
 *  by the validator, purely for compatibility); "timestamptz" (timestamp with time zone) is the real
 *  timezone-aware type "datetimeoffset" only happens to normalize to here. */
export const FM_ADD_COLUMN_DATA_TYPES_POSTGRES: readonly string[] = [
  'varchar(50)', 'varchar(120)', 'varchar(255)', 'text',
  'int', 'bigint', 'boolean',
  'date', 'timestamp', 'timestamptz',
  'decimal(18,2)',
  'uuid',
];

/** The type list to offer for the given wizard destination type — each engine sees its own native spelling,
 *  not a SQL-Server-flavored one that merely happens to be accepted by that engine's DDL validator. */
export function addColumnDataTypesFor(destType: string): readonly string[] {
  if (destType === 'fabricwarehouse') return FM_ADD_COLUMN_DATA_TYPES_FABRIC;
  if (destType === 'mysql') return FM_ADD_COLUMN_DATA_TYPES_MYSQL;
  if (destType === 'postgres') return FM_ADD_COLUMN_DATA_TYPES_POSTGRES;
  return FM_ADD_COLUMN_DATA_TYPES;
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
