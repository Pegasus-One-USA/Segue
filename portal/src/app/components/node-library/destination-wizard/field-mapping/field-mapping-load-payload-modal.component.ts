import { AfterViewInit, Component, ElementRef, ViewChild, input, output, signal } from '@angular/core';
import { samplePayloadJsonFor } from './field-mapping-payload.util';

/**
 * Modal for the mapping canvas's "⇩ Load JSON payload" affordance — paste a real FHIR resource (or
 * Bundle) and the source tree rebuilds directly from its actual shape (see
 * FieldMappingCanvasComponent.submitLoadPayload / parseSourcePayloadJson), replacing the built-in
 * field catalog for this resource only.
 */
@Component({
  selector: 'app-field-mapping-load-payload-modal',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-load-payload-modal.component.html',
  styleUrl: './field-mapping-load-payload-modal.component.scss',
})
export class FieldMappingLoadPayloadModalComponent implements AfterViewInit {
  @ViewChild('payloadInput') private readonly payloadInput?: ElementRef<HTMLTextAreaElement>;

  readonly resource = input.required<string>();
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

  useSample(): void {
    this.raw.set(samplePayloadJsonFor(this.resource()));
  }

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
