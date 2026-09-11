import { AfterViewInit, Component, ElementRef, ViewChild, input, output, signal } from '@angular/core';
import { DEFAULT_VALUE_PRESETS, DefaultValueToken } from './field-mapping-model';

export interface FmDefaultValueSubmit {
  token: DefaultValueToken;
  /** Only meaningful when token === '@default'. */
  literalValue: string | null;
  valueType: string;
}

/** Curated MappingValueType choices offered for a literal default — mirrors the subset
 *  CreateMappingProfileRequestValidator actually recognizes on the wire (MappingValueType enum). */
const VALUE_TYPES: readonly string[] = ['String', 'Integer', 'Decimal', 'Boolean', 'Date', 'DateTime'];

/**
 * Modal for the mapping canvas's "Set default value" affordance on a destination column — picks either a
 * literal constant (e.g. "Patient") or a pipeline/runtime @token preset (current timestamp, run id, …),
 * submitting REPLACES whatever mapping that column had (see FieldMappingCanvasComponent.submitDefaultValue),
 * which is what makes "once set to default, it can't also be mapped to a source field" true: there's only
 * ever one MappingRow per column, and this always writes a brand new one with no source at all.
 */
@Component({
  selector: 'app-field-mapping-default-value-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-default-value-modal.component.html',
  styleUrl: './field-mapping-default-value-modal.component.scss',
})
export class FieldMappingDefaultValueModalComponent implements AfterViewInit {
  @ViewChild('literalInput') private readonly literalInput?: ElementRef<HTMLInputElement>;

  readonly columnName = input.required<string>();
  readonly tableName = input.required<string>();
  /** Pre-fills the picker when re-opening this modal on a column already set to a default. */
  readonly existing = input<FmDefaultValueSubmit | null>(null);
  readonly error = input<string | null>(null);

  readonly submitted = output<FmDefaultValueSubmit>();
  /** Clears the default entirely, leaving the column unmapped — distinct from `cancelled` (closes without
   *  changing anything). */
  readonly removed = output<void>();
  readonly cancelled = output<void>();

  readonly presets = DEFAULT_VALUE_PRESETS;
  readonly valueTypes = VALUE_TYPES;

  readonly token = signal<DefaultValueToken>(this.existing()?.token ?? '@default');
  readonly literalValue = signal(this.existing()?.literalValue ?? '');
  readonly valueType = signal(this.existing()?.valueType ?? 'String');

  onTokenChange(value: string): void {
    const t = value as DefaultValueToken;
    this.token.set(t);
    // Adopt the preset's own declared type when switching to a runtime @token (nothing for the user to
    // second-guess there); a literal keeps whatever type was already picked, since it's genuinely free-form.
    const preset = this.presets.find(p => p.token === t);
    if (preset && t !== '@default') this.valueType.set(preset.valueType);
  }

  /** Template expressions can't contain arrow functions (Angular's NG5002) — computed here instead of
   *  inline so the description hint stays in sync with the currently-picked preset. */
  readonly selectedPresetDescription = () => this.presets.find(p => p.token === this.token())?.description ?? '';

  onLiteralInput(value: string): void { this.literalValue.set(value); }
  onValueTypeChange(value: string): void { this.valueType.set(value); }

  canSubmit(): boolean {
    return this.token() !== '@default' || this.literalValue().trim().length > 0;
  }

  ngAfterViewInit(): void {
    if (this.token() === '@default') this.literalInput?.nativeElement.focus();
  }

  onBackdropClick(ev: MouseEvent): void {
    if (ev.target === ev.currentTarget) this.cancelled.emit();
  }

  onEscape(): void {
    this.cancelled.emit();
  }

  submit(): void {
    if (!this.canSubmit()) return;
    this.submitted.emit({
      token: this.token(),
      literalValue: this.token() === '@default' ? this.literalValue().trim() : null,
      valueType: this.valueType(),
    });
  }
}
