import { Component, effect, inject, input, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { SourceConfigFormComponent } from '../../shared-v2/config-form/config-form-v2.contract';

/**
 * Minimal source-config form for an HL7 v2 / MLLP feed (SourceSystemType.Hl7v2). Unlike the FHIR-based vendors
 * (Epic, Cerner, Athenahealth, ...), HL7v2 has no prior dedicated backend field contract to mirror — the previous
 * UI only ever showed a static, read-only "Connection Details" panel (Protocol / Auth / Default Port) rather than
 * a real form. This is a deliberately small stub covering just what an MLLP listener needs to be configured
 * (host, port, receive timeout) — not a full build-out of HL7v2-specific retrieval/ack/segment-mapping options.
 */
@Component({
  selector: 'app-hl7v2-source-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './hl7v2-source-form.component.html',
  styleUrl: './hl7v2-source-form.component.scss',
})
export class Hl7v2SourceFormComponent implements SourceConfigFormComponent {
  private readonly fb = inject(FormBuilder);

  /** dest_*-style field bag from an existing node's config, when editing one already on the canvas — same
   *  pattern as GenericFhirSourceFormComponent.initialFields. */
  readonly initialFields = input<Record<string, string> | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: ['HL7 v2 / MLLP', [Validators.required]],
    host: ['', [Validators.required]],
    port: ['2575', [Validators.required, Validators.pattern(/^\d+$/)]],
    mllpTimeoutSeconds: ['30', [Validators.required, Validators.pattern(/^\d+$/)]],
  });

  constructor() {
    effect(() => {
      const fields = this.initialFields();
      untracked(() => {
        if (fields) this._populate(fields);
      });
    });
  }

  private _populate(fields: Record<string, string>): void {
    this.form.patchValue({
      name: fields['__name'] || 'HL7 v2 / MLLP',
      host: fields['Host'] || '',
      port: fields['Port'] || '2575',
      mllpTimeoutSeconds: fields['MLLP timeout (seconds)'] || '30',
    });
  }

  /** null ⇒ fix the highlighted fields — mirrors GenericFhirSourceFormComponent.getFields()'s contract. */
  getFields(): Record<string, string> | null {
    this.form.markAllAsTouched();
    if (this.form.invalid) return null;

    const v = this.form.getRawValue();
    return {
      __name: v.name || 'HL7 v2 / MLLP',
      Connector: 'HL7 v2 / MLLP',
      'App context': 'Backend system',
      Protocol: 'HL7 v2 over MLLP',
      Auth: 'None (MLLP TCP stream)',
      Host: v.host,
      Port: v.port,
      'MLLP timeout (seconds)': v.mllpTimeoutSeconds,
    };
  }
}
