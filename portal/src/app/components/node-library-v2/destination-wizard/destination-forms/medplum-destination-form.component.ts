import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';

/**
 * Medplum (FHIR R4 store) destination connection form — extracted from DestinationWizardComponent's inline
 * medplumForm during the inline-form → DESTINATION_FORM_REGISTRY refactor. Medplum is columnless (writes whole
 * resources), so there's no live schema probe; the FHIR base URL becomes the DestinationConfiguration.target
 * and the client secret / PEM private key is treated as a whole as the opaque secret (dest_medplumSecret),
 * same "single opaque secret" shape as Mongo's connectionString.
 */
@Component({
  selector: 'app-medplum-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  styleUrls: ['../destination-wizard.component.scss'],
  template: `
    <form [formGroup]="medplumForm" class="dw-form" autocomplete="off">
      <div class="dw-grid-2">
        <div class="dw-field" [class.dw-field--error]="medplumForm.get('name')!.invalid && medplumForm.get('name')!.touched">
          <label class="dw-label" for="dw-medplum-name">Destination name <span class="dw-req">*</span></label>
          <input id="dw-medplum-name" class="dw-input" formControlName="name" placeholder="e.g. Medplum Production" />
          @if (medplumForm.get('name')!.invalid && medplumForm.get('name')!.touched) {
            <span class="dw-error">Destination name is required.</span>
          }
        </div>

        <div class="dw-field dw-field--full" [class.dw-field--error]="medplumForm.get('baseUrl')!.invalid && medplumForm.get('baseUrl')!.touched">
          <label class="dw-label" for="dw-medplum-baseUrl">FHIR base URL <span class="dw-req">*</span></label>
          <input id="dw-medplum-baseUrl" class="dw-input" formControlName="baseUrl" placeholder="https://api.medplum.com/fhir/R4" />
          @if (medplumForm.get('baseUrl')!.invalid && medplumForm.get('baseUrl')!.touched) {
            <span class="dw-error">FHIR base URL is required.</span>
          }
        </div>

        <div class="dw-field" [class.dw-field--error]="medplumForm.get('clientId')!.invalid && medplumForm.get('clientId')!.touched">
          <label class="dw-label" for="dw-medplum-clientId">Client ID <span class="dw-req">*</span></label>
          <input id="dw-medplum-clientId" class="dw-input" formControlName="clientId" placeholder="e.g. 0193c5…" />
          @if (medplumForm.get('clientId')!.invalid && medplumForm.get('clientId')!.touched) {
            <span class="dw-error">Client ID is required.</span>
          }
        </div>

        <div class="dw-field" [class.dw-field--error]="medplumForm.get('secret')!.invalid && medplumForm.get('secret')!.touched">
          <label class="dw-label" for="dw-medplum-secret">Client secret / private key @if (!reusingExisting()) { <span class="dw-req">*</span> }</label>
          <input id="dw-medplum-secret" type="password" class="dw-input" formControlName="secret"
            [placeholder]="reusingExisting() ? 'Leave blank to keep the current client secret / private key' : 'client secret or PEM private key'"
            autocomplete="new-password" />
          @if (medplumForm.get('secret')!.invalid && medplumForm.get('secret')!.touched) {
            <span class="dw-error">A client secret or private key is required.</span>
          }
        </div>

        <div class="dw-field">
          <label class="dw-label" for="dw-medplum-authMethod">Auth method</label>
          <div class="dw-select-wrap">
            <select id="dw-medplum-authMethod" class="dw-select" formControlName="authMethod">
              <option value="client_secret">Client secret</option>
              <option value="private_key_jwt">Private key JWT</option>
            </select>
          </div>
        </div>

        <div class="dw-field">
          <label class="dw-label" for="dw-medplum-writeMode">Write mode</label>
          <div class="dw-select-wrap">
            <select id="dw-medplum-writeMode" class="dw-select" formControlName="writeMode">
              <option value="per_record">Per record</option>
              <option value="async_batch">Async batch</option>
            </select>
          </div>
        </div>

        <!-- Batch size is meaningful only for async_batch (records chunked into respond-async batch Bundles);
             per_record ignores it (one conditional PUT per record), so the field is hidden then. -->
        @if (medplumForm.get('writeMode')!.value === 'async_batch') {
          <div class="dw-field">
            <label class="dw-label" for="dw-medplum-batchSize">Batch size</label>
            <input id="dw-medplum-batchSize" class="dw-input" formControlName="batchSize" placeholder="100" />
          </div>
        }

        <div class="dw-field">
          <label class="dw-label" for="dw-medplum-identifierSystem">Identifier system</label>
          <input id="dw-medplum-identifierSystem" class="dw-input" formControlName="identifierSystem"
            placeholder="e.g. https://fhirbridge.example/mrn" />
        </div>
      </div>
    </form>

    <div class="sf-actions" style="margin-top: 20px;">
      <button type="button" class="dw-btn"
        [disabled]="probeState() === 'testing' || !canTest()"
        (click)="testConnection()">
        {{ probeState() === 'testing' ? 'Testing…' : 'Test Connection' }}
      </button>
    </div>
    @if (probeState() === 'testing') {
      <div class="dw-callout dw-callout--info" style="margin-top: 12px;">Testing connection…</div>
    }
    @if (probeState() === 'error') {
      <div class="dw-callout dw-callout--warn" style="margin-top: 12px;">Connection failed: {{ probeError() }}</div>
    }
    @if (probeState() === 'ok') {
      <div class="dw-callout dw-callout--info" style="margin-top: 12px;">Connected — Medplum reachable and credentials accepted.</div>
    }
  `,
})
export class MedplumDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  readonly medplumForm = this.fb.group({
    name: ['Medplum Production', [Validators.required]],
    // The FHIR R4 base URL — becomes the DestinationConfiguration.target (e.g. https://api.medplum.com/fhir/R4).
    baseUrl: ['', [Validators.required]],
    // The client secret OR PEM private key — treated as a whole as the opaque secret (dest_medplumSecret).
    // Never round-trips back from the API, same as Mongo's connectionString.
    secret: ['', [Validators.required]],
    clientId: ['', [Validators.required]],
    authMethod: ['client_secret', []], // or 'private_key_jwt'
    writeMode: ['per_record', []], // or 'async_batch'
    batchSize: ['100', []],
    identifierSystem: ['', []],
  });

  /** True while the host is reusing a previously-saved connection unchanged — secret is never repopulated when
   *  patching from an existing connection, so requiring it here would permanently block reuse unless the user
   *  retypes it just to satisfy validation. */
  readonly reusingExisting = input<boolean>(false);
  /** Not used by this form (Medplum's Test Connection has no server-side stored-secret resolution yet) — only
   *  declared so DestinationWizardComponent.activeFormInputs() can pass it uniformly to every registry-routed
   *  form without ComponentRef.setInput throwing on an undeclared input. */
  readonly existingDestinationId = input<string | null>(null);

  constructor() {
    effect(() => {
      const requireSecret = !this.reusingExisting();
      untracked(() => {
        const ctrl = this.medplumForm.get('secret')!;
        ctrl.setValidators(requireSecret ? [Validators.required] : []);
        ctrl.updateValueAndValidity({ emitEvent: false });
      });
    });
  }

  isValid(): boolean {
    return this.medplumForm.valid;
  }

  /** The Test Connection button needs the three fields the probe actually sends — base URL, client id, and the
   *  secret / private key. (The full form has more required fields, e.g. name, but they aren't part of the probe.) */
  canTest(): boolean {
    const v = this.medplumForm.value;
    return !!(v.baseUrl && v.clientId && v.secret);
  }

  /** Live connectivity check before saving: mints an OAuth2 token from the entered credentials and pings
   *  {baseUrl}/metadata server-side (see MedplumDestinationConnectionTestService). Never blocks Save. */
  testConnection(): void {
    if (!this.canTest()) return;
    const v = this.medplumForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc.testMedplum({
      baseUrl: v.baseUrl ?? '',
      clientId: v.clientId ?? '',
      secret: v.secret ?? '',
      authMethod: v.authMethod ?? 'client_secret',
    }).subscribe({
      next: res => {
        if (res.connected) {
          this.probeState.set('ok');
        } else {
          this.probeState.set('error');
          this.probeError.set(res.error ?? 'Connection failed.');
        }
      },
      error: err => {
        this.probeState.set('error');
        this.probeError.set(err?.error?.error ?? err?.error?.detail ?? err?.message ?? 'Connection failed.');
      },
    });
  }

  getRawValue(): Record<string, unknown> {
    return this.medplumForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.medplumForm.value;
    return {
      dest_name: v.name ?? '',
      dest_medplumBaseUrl: v.baseUrl ?? '',
      dest_medplumSecret: v.secret ?? '',
      dest_medplumClientId: v.clientId ?? '',
      dest_medplumAuthMethod: v.authMethod ?? 'client_secret',
      dest_medplumWriteMode: v.writeMode ?? 'per_record',
      dest_medplumBatchSize: v.batchSize ?? '100',
      dest_medplumIdentifierSystem: v.identifierSystem ?? '',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      // buildConnectionMetadata's default (csv/catch-all) branch carries the non-secret dest_medplum* keys,
      // stripping dest_medplumSecret — which is returned separately as the opaque connection secret.
      fields: JSON.parse(buildConnectionMetadata(config, 'csv')) as Record<string, string>,
      secret: config['dest_medplumSecret'] || '',
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.medplumForm.patchValue({
      name: fields['dest_name'] || this.medplumForm.value.name || 'Medplum Production',
      baseUrl: fields['dest_medplumBaseUrl'] || target || '',
      // Never repopulated — a secret, never returned by the API (matches Mongo's connectionString handling).
      secret: '',
      clientId: fields['dest_medplumClientId'] || '',
      authMethod: fields['dest_medplumAuthMethod'] || 'client_secret',
      writeMode: fields['dest_medplumWriteMode'] || 'per_record',
      batchSize: fields['dest_medplumBatchSize'] || '100',
      identifierSystem: fields['dest_medplumIdentifierSystem'] || '',
    });
  }

  reset(): void {
    this.medplumForm.reset({
      name: 'Medplum Production',
      baseUrl: '',
      secret: '',
      clientId: '',
      authMethod: 'client_secret',
      writeMode: 'per_record',
      batchSize: '100',
      identifierSystem: '',
    });
  }
}
