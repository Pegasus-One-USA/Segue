import { AfterViewInit, Component, ElementRef, OnInit, ViewChild, computed, input, output, signal } from '@angular/core';
import { FM_ADD_COLUMN_DATA_TYPES } from './field-mapping-add-column-modal.component';

export interface FmEditColumnSubmit {
  newColumnName: string;
  newDataType: string;
}

/**
 * Modal for the mapping canvas's column edit ("✎") affordance — rename a column and/or change its data
 * type, pre-filled with its current name/type. Submitting triggers a real ALTER TABLE (+ sp_rename when
 * the name changed) against the destination (see FieldMappingCanvasComponent.submitEditColumn). Only
 * shown while a live schema is known, mirroring FieldMappingAddColumnModalComponent's own gating.
 */
@Component({
  selector: 'app-field-mapping-edit-column-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-edit-column-modal.component.html',
  styleUrl: './field-mapping-edit-column-modal.component.scss',
})
export class FieldMappingEditColumnModalComponent implements OnInit, AfterViewInit {
  @ViewChild('nameInput') private readonly nameInput?: ElementRef<HTMLInputElement>;

  readonly tableName = input.required<string>();
  readonly currentColumnName = input.required<string>();
  /** The column's current real data type — falls back to the first offered type when unknown (e.g. a
   *  free-text/never-probed column), same as the add-column modal's own default. */
  readonly currentDataType = input<string | undefined>(undefined);
  readonly submitting = input<boolean>(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<FmEditColumnSubmit>();
  readonly cancelled = output<void>();

  // Seeded from the inputs in ngOnInit — NOT here. Field initializers run during construction, before
  // Angular has written bound input values, so reading a required input signal (e.g. currentColumnName())
  // at this point throws NG0951 ("input is required but no value is available yet"), which silently
  // kills this component's construction (and with it, the whole modal never appears).
  readonly columnName = signal('');
  readonly dataType = signal<string>(FM_ADD_COLUMN_DATA_TYPES[2]);

  /** The curated list, plus the column's own current type prepended when it isn't already one of the
   *  curated options (e.g. "nvarchar(500)") — otherwise the <select> would silently show its first
   *  option instead of the real current value, making an untouched save look like a type change.
   *  computed() is safe here (unlike the field initializers above) because its callback only runs when
   *  first read, which happens during change detection — well after inputs are set. */
  readonly dataTypes = computed(() => {
    const current = this.currentDataType();
    return current && !FM_ADD_COLUMN_DATA_TYPES.includes(current)
      ? [current, ...FM_ADD_COLUMN_DATA_TYPES]
      : FM_ADD_COLUMN_DATA_TYPES;
  });

  canSubmit(): boolean {
    return this.columnName().trim().length > 0 && !this.submitting();
  }

  ngOnInit(): void {
    this.columnName.set(this.currentColumnName());
    this.dataType.set(this.currentDataType() ?? FM_ADD_COLUMN_DATA_TYPES[2]);
  }

  ngAfterViewInit(): void {
    this.nameInput?.nativeElement.focus();
    this.nameInput?.nativeElement.select();
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
    this.submitted.emit({ newColumnName: this.columnName().trim(), newDataType: this.dataType() });
  }
}
