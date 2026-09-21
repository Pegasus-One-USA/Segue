import { AfterViewInit, Component, ElementRef, ViewChild, input, output, signal } from '@angular/core';

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

  readonly dataTypes = FM_ADD_COLUMN_DATA_TYPES;
  readonly columnName = signal('');
  readonly dataType = signal<string>(FM_ADD_COLUMN_DATA_TYPES[2]);

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
