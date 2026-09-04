import { AfterViewInit, Component, ElementRef, OnInit, ViewChild, input, output, signal } from '@angular/core';

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
export class FieldMappingLoadPayloadModalComponent implements OnInit, AfterViewInit {
  @ViewChild('payloadInput') private readonly payloadInput?: ElementRef<HTMLTextAreaElement>;

  readonly resource = input.required<string>();
  readonly submitting = input<boolean>(false);
  readonly error = input<string | null>(null);
  /** Whatever the canvas's source tree is ACTUALLY showing for this resource right now — a previously
   *  loaded/edited payload if there is one, the true default catalog otherwise (see
   *  FieldMappingCanvasComponent.currentPayloadJsonFor). Pre-fills the textarea every time this modal
   *  opens, so re-opening it shows what's really on the canvas, never a stale/unrelated default. */
  readonly currentRaw = input.required<string>();
  /** The TRUE original/default payload for this resource, independent of any override currently active
   *  (see FieldMappingCanvasComponent.originalPayloadJsonFor) — what "Reset to Original" restores the
   *  textarea to, deliberately distinct from currentRaw above. */
  readonly originalRaw = input.required<string>();

  readonly submitted = output<string>();
  readonly cancelled = output<void>();
  /** "Reset to Original" was clicked — the host clears this resource's existing mappings for it (same
   *  reasoning as a real submit: the textarea's content just changed out from under whatever was mapped
   *  against it), even though nothing was actually re-parsed/submitted here. */
  readonly resetToOriginal = output<void>();

  readonly raw = signal('');

  ngOnInit(): void {
    // Not the constructor — signal inputs only reflect their bound value once Angular has actually
    // applied bindings (same reasoning as FieldMappingCanvasComponent.ngOnInit's own identical comment on
    // initialZoom). Runs once: this modal is a fresh instance every time it opens (an @if-conditional
    // sibling in the canvas template), and currentRaw never changes for the rest of this instance's life.
    // currentRaw, not originalRaw — opening this modal must show what's CURRENTLY on the canvas, not the
    // true default (that's what the separate "Reset to Original" button below is for).
    this.raw.set(this.currentRaw());
  }

  ngAfterViewInit(): void {
    this.payloadInput?.nativeElement.focus();
  }

  canSubmit(): boolean {
    return this.raw().trim().length > 0 && !this.submitting();
  }

  onInput(value: string): void { this.raw.set(value); }

  onResetToOriginal(): void {
    this.raw.set(this.originalRaw());
    this.resetToOriginal.emit();
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
