import { Component, input, signal, forwardRef } from '@angular/core';
import { NG_VALUE_ACCESSOR, ControlValueAccessor } from '@angular/forms';

/**
 * Reactive-forms-compatible (ControlValueAccessor) image field: paste a URL or
 * upload a file (read as a data-URL — there's no real asset storage backend yet,
 * so this is purely an in-memory/localStorage-mock preview), with a live thumbnail.
 * Reused for every brand image field (logo, dark logo, favicon, login background,
 * login illustration, email logo) instead of repeating the same markup six times.
 */
@Component({
  selector: 'app-brand-asset-field',
  standalone: true,
  templateUrl: './brand-asset-field.component.html',
  styleUrl: './brand-asset-field.component.scss',
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => BrandAssetFieldComponent), multi: true },
  ],
})
export class BrandAssetFieldComponent implements ControlValueAccessor {
  private static nextId = 0;

  readonly label = input.required<string>();
  readonly hint  = input<string>('');

  /** Unique per instance — six of these render on one page, so a static id would collide. */
  protected readonly fieldId = `baf-${BrandAssetFieldComponent.nextId++}`;

  protected readonly value    = signal('');
  protected readonly disabled = signal(false);

  private onChange?: (v: string) => void;
  protected onTouched?: () => void;

  writeValue(v: string | null): void {
    this.value.set(v ?? '');
  }

  registerOnChange(fn: (v: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  protected onUrlInput(v: string): void {
    this.value.set(v);
    this.onChange?.(v);
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = reader.result as string;
      this.value.set(dataUrl);
      this.onChange?.(dataUrl);
      this.onTouched?.();
    };
    reader.readAsDataURL(file);
    input.value = '';
  }

  protected clear(): void {
    this.value.set('');
    this.onChange?.('');
    this.onTouched?.();
  }
}
