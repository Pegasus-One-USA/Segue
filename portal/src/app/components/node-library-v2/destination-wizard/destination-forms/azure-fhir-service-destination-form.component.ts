import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata, buildFhirSecretBlob } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * Azure FHIR Service (Azure Health Data Services) destination connection form. Azure's FHIR API is always
 * secured by Azure AD (no anonymous option, unlike the generic FhirRepository stub), so — unlike that stub —
 * this form always collects real credentials, toggled between the two Azure-native auth shapes:
 * `clientCredentials` (an Entra ID app registration's tenant/client id + secret) and `managedIdentity` (no
 * secret at all; FHIRBridge's own compute identity authenticates directly). Mirrors
 * BlobStorageDestinationFormComponent's auth-mode-conditional-validator pattern (the only other form with a
 * managed-identity-vs-secret toggle) rather than Medplum's simpler single-secret shape.
 *
 * The "Managed identity" option is currently commented out of the auth-mode dropdown (same as
 * BlobStorageDestinationFormComponent's own managedIdentity/servicePrincipal options) — nothing in this form's
 * logic, validators, getFullConfig()/patchFrom(), or the backend (FhirRepositoryAuthResolver, the writer, Test
 * Connection) changed, so a destination already saved with managedIdentity keeps working unchanged; it just
 * can't be newly selected from this dropdown for now.
 */
@Component({
  selector: 'app-azure-fhir-service-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  styleUrls: ['../destination-wizard.component.scss'],
  template: `
    <form [formGroup]="fhirForm" class="dw-form" autocomplete="off">
      <div class="dw-grid-2">
        <div class="dw-field" [class.dw-field--error]="fhirForm.get('name')!.invalid && fhirForm.get('name')!.touched">
          <label class="dw-label" for="dw-azfhir-name">Destination name <span class="dw-req">*</span></label>
          <input id="dw-azfhir-name" class="dw-input" formControlName="name" placeholder="e.g. Contoso AHDS Prod" />
          @if (fhirForm.get('name')!.invalid && fhirForm.get('name')!.touched) {
            <span class="dw-error">Destination name is required.</span>
          }
        </div>

        <div class="dw-field dw-field--full" [class.dw-field--error]="fhirForm.get('baseUrl')!.invalid && fhirForm.get('baseUrl')!.touched">
          <label class="dw-label" for="dw-azfhir-baseUrl">FHIR service URL <span class="dw-req">*</span></label>
          <input id="dw-azfhir-baseUrl" class="dw-input" formControlName="baseUrl"
            placeholder="https://myworkspace-myfhirservice.fhir.azurehealthcareapis.com" />
          @if (fhirForm.get('baseUrl')!.invalid && fhirForm.get('baseUrl')!.touched) {
            <span class="dw-error">FHIR service URL is required.</span>
          }
        </div>

        <div class="dw-field">
          <label class="dw-label" for="dw-azfhir-authMode">Authentication</label>
          <div class="dw-select-wrap">
            <select id="dw-azfhir-authMode" class="dw-select" formControlName="authMode">
              <option value="clientCredentials">Client credentials (service principal)</option>
              <!-- Hidden for now — re-enable when ready. Form support is untouched (mirrors
                   BlobStorageDestinationFormComponent's own managedIdentity/servicePrincipal hiding). -->
              <!-- <option value="managedIdentity">Managed identity</option> -->
            </select>
          </div>
        </div>

        @if (fhirForm.get('authMode')!.value === 'clientCredentials') {
          <div class="dw-field" [class.dw-field--error]="fhirForm.get('tenantId')!.invalid && fhirForm.get('tenantId')!.touched">
            <label class="dw-label" for="dw-azfhir-tenantId">Tenant ID <span class="dw-req">*</span></label>
            <input id="dw-azfhir-tenantId" class="dw-input" formControlName="tenantId" placeholder="e.g. 72f9…" />
            @if (fhirForm.get('tenantId')!.invalid && fhirForm.get('tenantId')!.touched) {
              <span class="dw-error">Tenant ID is required.</span>
            }
          </div>

          <div class="dw-field" [class.dw-field--error]="fhirForm.get('clientId')!.invalid && fhirForm.get('clientId')!.touched">
            <label class="dw-label" for="dw-azfhir-clientId">Client ID <span class="dw-req">*</span></label>
            <input id="dw-azfhir-clientId" class="dw-input" formControlName="clientId" placeholder="e.g. 0193c5…" />
            @if (fhirForm.get('clientId')!.invalid && fhirForm.get('clientId')!.touched) {
              <span class="dw-error">Client ID is required.</span>
            }
          </div>

          <div class="dw-field" [class.dw-field--error]="fhirForm.get('clientSecret')!.invalid && fhirForm.get('clientSecret')!.touched">
            <label class="dw-label" for="dw-azfhir-clientSecret">Client secret @if (!reusingExisting()) { <span class="dw-req">*</span> }</label>
            <input id="dw-azfhir-clientSecret" type="password" class="dw-input" formControlName="clientSecret"
              [placeholder]="reusingExisting() ? 'Leave blank to keep the current client secret' : 'client secret'" autocomplete="new-password" />
            @if (fhirForm.get('clientSecret')!.invalid && fhirForm.get('clientSecret')!.touched) {
              <span class="dw-error">A client secret is required.</span>
            }
          </div>
        } @else {
          <div class="dw-field">
            <label class="dw-label" for="dw-azfhir-managedIdentityClientId">User-assigned identity client ID (optional)</label>
            <input id="dw-azfhir-managedIdentityClientId" class="dw-input" formControlName="managedIdentityClientId"
              placeholder="Blank uses the system-assigned identity" />
          </div>
        }

        <div class="dw-field">
          <label class="dw-label" for="dw-azfhir-scope">Scope override (optional)</label>
          <input id="dw-azfhir-scope" class="dw-input" formControlName="scope"
            placeholder="Defaults to {FHIR service URL}/.default" />
        </div>

        <div class="dw-field dw-field--full">
          <label class="dw-label">
            <input type="checkbox" formControlName="autoFetchMissingReferences" />
            Auto-fetch missing references from the source EHR
          </label>
          <span class="dw-hint">
            When a resource references another (e.g. a Patient's managing Organization) that isn't in the same
            batch or already at the destination, fetch it from the source on demand instead of blocking the write.
          </span>
        </div>

        @if (fhirForm.get('autoFetchMissingReferences')!.value) {
          <div class="dw-field">
            <label class="dw-label" for="dw-azfhir-autoFetchMaxCount">Auto-fetch limit per run</label>
            <input id="dw-azfhir-autoFetchMaxCount" class="dw-input" formControlName="autoFetchMaxCount" placeholder="25" />
          </div>
        }

        <div class="dw-field dw-field--full">
          <button type="button" class="dw-btn" [disabled]="!canTestConnection() || probeState() === 'testing'" (click)="testConnection()">
            @if (probeState() === 'testing') { Testing… } @else { Test Connection }
          </button>
          @if (probeState() === 'error') {
            <div class="dw-callout dw-callout--warn">Connection failed: {{ probeError() }}</div>
          }
          @if (probeState() === 'ok') {
            <div class="dw-callout dw-callout--info">Connected — the FHIR service URL and credentials are valid.</div>
          }
        </div>
      </div>
    </form>
  `,
})
export class AzureFhirServiceDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  /** Mirrors the SQL-family/CSV/SFTP forms' own probeState/probeError pair (see e.g.
   *  sql-family-destination-form.component.html) — this form owns its own copy since it isn't part of the
   *  shared SqlFamilyFormApi contract. */
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  /** True while the host is reusing a previously-saved connection unchanged — clientSecret is a secret that is
   *  never repopulated when patching from an existing connection, so requiring it here would permanently block
   *  reuse unless the user retypes it just to satisfy validation. Mirrors BlobStorageDestinationFormComponent. */
  readonly reusingExisting = input<boolean>(false);
  /** The already-saved destination's id when reusing it unchanged — lets Test Connection resolve the stored
   *  secret server-side (see FhirDestinationConnectionTestService.ResolveStoredSecretAsync) instead of requiring
   *  the client secret retyped, mirroring SqlFamilyDestinationFormComponent/MongoDestinationFormComponent/
   *  BlobStorageDestinationFormComponent's identical existingDestinationId input. */
  readonly existingDestinationId = input<string | null>(null);

  readonly fhirForm = this.fb.group({
    name: ['Azure FHIR Service', [Validators.required]],
    baseUrl: ['', [Validators.required]],
    authMode: ['clientCredentials', []], // or 'managedIdentity'
    tenantId: ['', []],
    clientId: ['', []],
    clientSecret: ['', []],
    managedIdentityClientId: ['', []],
    scope: ['', []],
    autoFetchMissingReferences: [false, []],
    autoFetchMaxCount: ['25', []],
  });

  constructor() {
    effect(() => {
      const authMode = this.fhirForm.controls.authMode.value;
      const reusing = this.reusingExisting();
      untracked(() => this._syncAuthModeValidators(authMode, reusing));
    });
    this.fhirForm.controls.authMode.valueChanges.subscribe(v =>
      this._syncAuthModeValidators(v, this.reusingExisting()));
  }

  /** Client credentials needs tenantId/clientId/clientSecret; managed identity needs none of them (and never
   *  resolves a Key Vault secret at all — see FhirRepositoryAuthResolver's "managedidentity" branch). clientSecret
   *  is only required for a genuinely new connection, since reusingExisting never repopulates it. */
  private _syncAuthModeValidators(authMode: string | null, reusingExisting: boolean): void {
    // A prior "Connected" / error result no longer applies once the auth mode (or its fields) changes.
    this.probeState.set('idle');
    this.probeError.set(null);

    const isClientCredentials = authMode === 'clientCredentials';

    const tenantCtrl = this.fhirForm.get('tenantId')!;
    tenantCtrl.setValidators(isClientCredentials ? [Validators.required] : []);
    tenantCtrl.updateValueAndValidity({ emitEvent: false });

    const clientCtrl = this.fhirForm.get('clientId')!;
    clientCtrl.setValidators(isClientCredentials ? [Validators.required] : []);
    clientCtrl.updateValueAndValidity({ emitEvent: false });

    const secretCtrl = this.fhirForm.get('clientSecret')!;
    secretCtrl.setValidators(isClientCredentials && !reusingExisting ? [Validators.required] : []);
    secretCtrl.updateValueAndValidity({ emitEvent: false });
  }

  /** Base URL is always required to test; client credentials mode additionally needs tenant/client, plus either
   *  a typed secret or a stored one to fall back to (reusing an existing destination unchanged — the secret is
   *  never repopulated, so requiring it retyped here blocked Test Connection entirely; testConnection() below
   *  passes existingDestinationId() so the backend resolves the stored one instead). */
  canTestConnection(): boolean {
    const v = this.fhirForm.value;
    if (!v.baseUrl) return false;
    if (v.authMode === 'clientCredentials') {
      return !!(v.tenantId && v.clientId && (v.clientSecret || (this.reusingExisting() && this.existingDestinationId())));
    }
    return true;
  }

  /** Tests the connection with the form's CURRENT (not-yet-saved) field values, falling back to the stored
   *  secret server-side when reusing an existing destination unchanged and clientSecret was left blank (see
   *  canTestConnection()). Backed by POST /destination-schema/fhir-test (FhirDestinationConnectionTestService),
   *  the same ad-hoc test endpoint the generic Aidbox form's testFhirConnection() uses, extended to accept
   *  tenantId (skips SMART-configuration discovery, computing the Entra ID token endpoint directly instead) and
   *  a managedIdentity auth type. */
  testConnection(): void {
    if (!this.canTestConnection()) return;
    const v = this.fhirForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc
      .testFhir({
        baseUrl: v.baseUrl ?? '',
        authType: v.authMode ?? 'clientCredentials',
        clientId: v.authMode === 'clientCredentials' ? (v.clientId ?? undefined) : undefined,
        clientSecret: v.authMode === 'clientCredentials' ? (v.clientSecret ?? undefined) : undefined,
        tenantId: v.authMode === 'clientCredentials' ? (v.tenantId ?? undefined) : undefined,
        scope: v.scope || undefined,
        managedIdentityClientId: v.authMode === 'managedIdentity' ? (v.managedIdentityClientId ?? undefined) : undefined,
        destinationId: this.reusingExisting() ? (this.existingDestinationId() ?? undefined) : undefined,
      })
      .subscribe({
        next: (res) => {
          if (!res.connected) {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
            return;
          }
          this.probeState.set('ok');
        },
        error: (err) => {
          this.probeState.set('error');
          this.probeError.set(
            typeof err?.error?.error === 'string' ? err.error.error : 'Connection failed.',
          );
        },
      });
  }

  isValid(): boolean {
    return this.fhirForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.fhirForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.fhirForm.value;
    const authMode = v.authMode ?? 'clientCredentials';
    return {
      dest_name: v.name ?? '',
      dest_baseUrl: v.baseUrl ?? '',
      // Sets buildConnectionMetadata's kind==='fhir' dest_authType -> dest_fhirAuthType bridge — both values used
      // here ('clientCredentials'/'managedIdentity') already match the backend's real dest_fhirAuthType vocabulary
      // (see FhirRepositoryAuthResolver), so the bridge's ternary passes them through unchanged.
      dest_authType: authMode,
      dest_clientId: v.clientId ?? '',
      dest_clientSecret: v.clientSecret ?? '',
      dest_tokenEndpoint: authMode === 'clientCredentials'
        ? `https://login.microsoftonline.com/${v.tenantId ?? ''}/oauth2/v2.0/token`
        : '',
      dest_fhirAzureScope: v.scope ?? '',
      dest_fhirManagedIdentityClientId: v.managedIdentityClientId ?? '',
      dest_autoFetchMissingReferences: v.autoFetchMissingReferences ? 'true' : 'false',
      dest_autoFetchMaxCount: v.autoFetchMaxCount ?? '25',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    // Reusing an already-saved client-credentials connection with the secret left blank means "keep what's
    // already stored" — never bake a blank clientSecret into a rebuilt {clientId, clientSecret, tokenEndpoint}
    // blob, which would silently overwrite the real stored secret.
    const keepExisting =
      this.reusingExisting() && config['dest_authType'] === 'clientCredentials' && !this.fhirForm.value.clientSecret;
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'fhir')) as Record<string, string>,
      // Managed identity never resolves a Key Vault secret — buildFhirSecretBlob returns '' for it. Client
      // credentials returns the {clientId, clientSecret, tokenEndpoint} blob FhirRepositoryAuthResolver expects.
      secret: keepExisting ? null : buildFhirSecretBlob(config),
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    // Two different shapes can reach here, both needing to be handled: "select existing connection" patches
    // from the real persisted ConnectionMetadataJson (dest_fhirAuthType — what buildConnectionMetadata's
    // bridge writes), but editing an already-saved CANVAS NODE (destination-wizard.component.ts's
    // _populateFromNode()) instead passes the node's own raw dest_* fields verbatim — which still carry this
    // form's own internal key, dest_authType, never bridged at that layer. Checking dest_fhirAuthType only
    // meant re-opening a saved managedIdentity node always silently fell back to clientCredentials.
    const rawAuthMode = fields['dest_fhirAuthType'] ?? fields['dest_authType'];
    const authMode = rawAuthMode === 'managedIdentity' ? 'managedIdentity' : 'clientCredentials';
    const tokenEndpoint = fields['dest_tokenEndpoint'] ?? '';
    const tenantIdMatch = /login\.microsoftonline\.com\/([^/]+)\//.exec(tokenEndpoint);

    this.fhirForm.patchValue({
      name: fields['dest_name'] || this.fhirForm.value.name || 'Azure FHIR Service',
      baseUrl: fields['dest_baseUrl'] || target || '',
      authMode,
      tenantId: tenantIdMatch?.[1] ?? '',
      clientId: fields['dest_clientId'] || '',
      // Never repopulated — a secret, never returned by the API (matches Mongo's connectionString handling).
      clientSecret: '',
      managedIdentityClientId: fields['dest_fhirManagedIdentityClientId'] || '',
      scope: fields['dest_fhirAzureScope'] || '',
      autoFetchMissingReferences: fields['dest_autoFetchMissingReferences'] === 'true',
      autoFetchMaxCount: fields['dest_autoFetchMaxCount'] || '25',
    });
  }

  reset(): void {
    this.probeState.set('idle');
    this.probeError.set(null);
    this.fhirForm.reset({
      name: 'Azure FHIR Service',
      baseUrl: '',
      authMode: 'clientCredentials',
      tenantId: '',
      clientId: '',
      clientSecret: '',
      managedIdentityClientId: '',
      scope: '',
      autoFetchMissingReferences: false,
      autoFetchMaxCount: '25',
    });
  }
}
