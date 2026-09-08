import { Component, computed, input, output, signal } from '@angular/core';
import { MappingSummaryDocument } from './field-mapping-summary.model';

/**
 * Shows the canonical Mapping JSON (see field-mapping-summary.model.ts) built when the user clicks
 * "Save mapping" — lets them confirm/copy the exact document that was (or will be) sent to the backend.
 */
@Component({
  selector: 'app-field-mapping-export-preview-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-export-preview-modal.component.html',
  styleUrl: './field-mapping-export-preview-modal.component.scss',
})
export class FieldMappingExportPreviewModalComponent {
  readonly data = input<MappingSummaryDocument | null>(null);

  readonly closed = output<void>();

  readonly copied = signal(false);

  readonly jsonText = computed(() => {
    const d = this.data();
    return d ? JSON.stringify(d, null, 2) : '';
  });

  copyJson(): void {
    navigator.clipboard.writeText(this.jsonText()).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    });
  }

  onBackdropClick(ev: MouseEvent): void {
    if (ev.target === ev.currentTarget) this.closed.emit();
  }

  onEscape(): void {
    this.closed.emit();
  }
}
