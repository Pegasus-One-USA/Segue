import { AfterViewInit, Component, ElementRef, ViewChild, input, output, signal } from '@angular/core';

/**
 * Modal for a destination card's "⇩ Load JSON payload" affordance (file-shaped destinations only — CSV/
 * Blob/Data Lake/Fabric/API Endpoint, the same set field-mapping-target-card.component.html's free-text
 * "Add new column" row already covers) — paste the target system's own expected JSON body and the
 * card's column list rebuilds from its actual shape (see
 * FieldMappingCanvasComponent.submitLoadDestinationPayload / parseDestinationPayloadJson), replacing
 * whatever free-text columns this table already had.
 *
 * Deliberately simpler than FieldMappingLoadPayloadModalComponent (the source-side equivalent): there is
 * no backend catalog or built-in field list for a destination table to fall back to, so there is no
 * "Reset to Original" concept here and no pre-filled textarea — just paste and load.
 */
@Component({
  selector: 'app-field-mapping-load-destination-payload-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-load-destination-payload-modal.component.html',
  styleUrl: './field-mapping-load-destination-payload-modal.component.scss',
})
export class FieldMappingLoadDestinationPayloadModalComponent implements AfterViewInit {
  @ViewChild('payloadInput') private readonly payloadInput?: ElementRef<HTMLTextAreaElement>;

  readonly tableLabel = input.required<string>();
  readonly submitting = input<boolean>(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<string>();
  readonly cancelled = output<void>();

  readonly raw = signal('');

  ngAfterViewInit(): void {
    this.payloadInput?.nativeElement.focus();
  }

  canSubmit(): boolean {
    return this.raw().trim().length > 0 && !this.submitting();
  }

  onInput(value: string): void { this.raw.set(value); }

  submit(): void {
    if (!this.canSubmit()) return;
    this.submitted.emit(this.raw());
  }

  onBackdropClick(ev: MouseEvent): void {
    if (ev.target === ev.currentTarget && !this.submitting()) this.cancelled.emit();
  }

  onEscape(): void {
    if (!this.submitting()) this.cancelled.emit();
  }
}
