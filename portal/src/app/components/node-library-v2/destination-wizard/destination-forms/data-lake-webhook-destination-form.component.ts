import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * Data Lake Webhook destination — data-plane HTTP delivery of mapped records to a lake ingestion endpoint
 * (Fabric Eventstream custom endpoint, Databricks/Snowpipe Streaming REST, an API-Gateway front door over S3,
 * Splunk HEC, Event Grid).
 *
 * Worth distinguishing from the two things it sits next to in the catalog: the REST API destination posts one
 * record per request with no auth beyond the raw secret and no retry, and the workflow canvas's Webhook Notify
 * node carries only a write SUMMARY and refuses record-level data. This one carries the records, which is why
 * the endpoint is https-validated inline here (mirroring DataLakeWebhookSettings.ValidateEndpointUrl and
 * CreateDestinationConfigurationRequestValidator server-side) and why "Include source FHIR resource" defaults off.
 */
@Component({
  selector: 'app-data-lake-webhook-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './data-lake-webhook-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
  // Scoped to this component rather than added to the shared wizard stylesheet: the disclosure is the only
  // thing that needs it, and keeping it here means no other destination form's markup can be affected.
  styles: [`
    .dlw-adv { margin-top: 4px; }
    .dlw-adv-toggle {
      display: flex; align-items: center; gap: 8px; width: 100%;
      background: none; border: 0; border-top: 1px solid var(--color-border);
      padding: 14px 0 0; cursor: pointer; text-align: left;
      font: inherit; font-weight: 600; color: var(--color-text);
    }
    .dlw-adv-toggle:hover { color: var(--color-primary, #00A89D); }
    .dlw-adv-toggle:focus-visible { outline: 2px solid var(--color-primary, #00A89D); outline-offset: 2px; }
    .dlw-adv-caret { display: inline-block; transition: transform 120ms ease; color: var(--color-text-muted, #6b7280); }
    .dlw-adv-caret--open { transform: rotate(90deg); }
    @media (prefers-reduced-motion: reduce) { .dlw-adv-caret { transition: none; } }
    .dlw-adv-sub { font-weight: 400; font-size: 12px; color: var(--color-text-muted, #6b7280); }
    .dlw-adv-badge {
      font-weight: 600; font-size: 11px; letter-spacing: .04em; text-transform: uppercase;
      color: var(--color-error, #b32020);
    }
  `],
})
export class DataLakeWebhookDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);

  /** https everywhere, with loopback http allowed for a local collector — same exception the backend makes. */
  static readonly ENDPOINT_URL_PATTERN =
    /^(https:\/\/\S+|http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:\d+)?(\/\S*)?)$/;

  readonly webhookForm = this.fb.group({
    name: ['Data Lake Webhook', [Validators.required]],
    // Blank is legal only for auth mode "none", where the stored secret is itself a pre-authorized ingest URL
    // (the shape a Fabric Eventstream / Event Grid endpoint takes) — see _syncAuthModeValidators.
    endpointUrl: ['', [Validators.pattern(DataLakeWebhookDestinationFormComponent.ENDPOINT_URL_PATTERN)]],
    authMode: ['bearer', [Validators.required]],
    // The one secret control for every auth mode (bearer token / API key / "user:password" / HMAC shared secret /
    // OAuth2 client secret) — its label swaps per authMode in the template. Never repopulated when reusing an
    // existing connection, same as the Blob and SFTP forms.
    secretValue: ['', []],
    authHeaderName: ['', []],
    signatureHeaderName: ['', []],
    timestampHeaderName: ['', []],
    tokenEndpoint: ['', []],
    clientId: ['', []],
    scope: ['', []],
    payloadShape: ['ndjson', [Validators.required]],
    httpMethod: ['POST', [Validators.required]],
    contentType: ['', []],
    compression: ['none', []],
    batchSize: [500, [Validators.required, Validators.min(1), Validators.max(50000)]],
    maxRequestBytes: [4194304, [Validators.required, Validators.min(1024), Validators.max(100663296)]],
    timeoutSeconds: [30, [Validators.required, Validators.min(1), Validators.max(600)]],
    retryCount: [3, [Validators.required, Validators.min(0), Validators.max(10)]],
    retryBackoffSeconds: [2, [Validators.min(0), Validators.max(60)]],
    expectedStatusCodes: ['', []],
    headersJson: ['', [DataLakeWebhookDestinationFormComponent.jsonObjectValidator]],
    // Off by default on purpose: the mapped values have been through the mapping profile and the destination's
    // de-identification profile, while the raw source resource is the whole FHIR document.
    includeSourceJson: [false, []],
    onFailure: ['fail', [Validators.required]],
  });

  /** Blank passes — an empty headers box means "no static headers", not "invalid". */
  private static jsonObjectValidator(control: { value: unknown }): Record<string, boolean> | null {
    const raw = (control.value ?? '') as string;
    if (!raw.trim()) return null;
    try {
      const parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? null : { jsonObject: true };
    } catch {
      return { jsonObject: true };
    }
  }

  /** True while the host is reusing a previously-saved connection unchanged — the secret is never repopulated
   *  when patching from an existing connection, so requiring it would block reuse unless the user retyped it. */
  readonly reusingExisting = input<boolean>(false);

  /** Whether the Advanced disclosure is expanded. Collapsed by default — every control inside it has a
   *  working default, so a first-time connection never needs to open it. */
  readonly advancedOpen = signal(false);

  toggleAdvanced(): void {
    this.advancedOpen.update(open => !open);
  }

  /** Controls that live inside the Advanced disclosure. Listed once here so the template's force-open check
   *  and this class stay in step. */
  private static readonly ADVANCED_CONTROLS = [
    'httpMethod', 'compression', 'maxRequestBytes', 'timeoutSeconds', 'retryCount',
    'retryBackoffSeconds', 'contentType', 'expectedStatusCodes', 'scope',
    'signatureHeaderName', 'timestampHeaderName', 'headersJson',
  ];

  /**
   * True when any Advanced control is invalid and has been touched — the template then force-opens the
   * section. Without this, a min/max violation on (say) Timeout would block Save from inside a collapsed
   * panel, with the offending field and its error message both hidden and nothing on screen to explain why.
   */
  hasAdvancedError(): boolean {
    return DataLakeWebhookDestinationFormComponent.ADVANCED_CONTROLS.some(name => {
      const control = this.webhookForm.get(name);
      return !!control && control.invalid && control.touched;
    });
  }

  constructor() {
    effect(() => {
      const authMode = this.webhookForm.controls.authMode.value;
      const reusing = this.reusingExisting();
      untracked(() => this._syncAuthModeValidators(authMode, reusing));
    });
    this.webhookForm.controls.authMode.valueChanges.subscribe(v =>
      this._syncAuthModeValidators(v, this.reusingExisting()));
  }

  /** Mirrors the backend's DataLakeWebhookSettings.Parse rules: the endpoint is required for every mode except
   *  "none" (where the secret carries it); API-key mode needs a header name; OAuth2 needs a token endpoint and
   *  client id; and every mode except "none" needs credential material — but only for a genuinely new
   *  connection, since reusingExisting never repopulates the secret. */
  private _syncAuthModeValidators(authMode: string | null, reusingExisting: boolean): void {
    const urlCarriesCredential = authMode === 'none';

    const endpointCtrl = this.webhookForm.get('endpointUrl')!;
    endpointCtrl.setValidators(
      urlCarriesCredential
        ? [Validators.pattern(DataLakeWebhookDestinationFormComponent.ENDPOINT_URL_PATTERN)]
        : [Validators.required, Validators.pattern(DataLakeWebhookDestinationFormComponent.ENDPOINT_URL_PATTERN)]);
    endpointCtrl.updateValueAndValidity({ emitEvent: false });

    const secretCtrl = this.webhookForm.get('secretValue')!;
    secretCtrl.setValidators(!urlCarriesCredential && !reusingExisting ? [Validators.required] : []);
    secretCtrl.updateValueAndValidity({ emitEvent: false });

    const headerNameCtrl = this.webhookForm.get('authHeaderName')!;
    headerNameCtrl.setValidators(authMode === 'apiKeyHeader' ? [Validators.required] : []);
    headerNameCtrl.updateValueAndValidity({ emitEvent: false });

    const requiresOAuth2 = authMode === 'oauth2ClientCredentials';
    const tokenEndpointCtrl = this.webhookForm.get('tokenEndpoint')!;
    tokenEndpointCtrl.setValidators(requiresOAuth2 ? [Validators.required] : []);
    tokenEndpointCtrl.updateValueAndValidity({ emitEvent: false });
    const clientIdCtrl = this.webhookForm.get('clientId')!;
    clientIdCtrl.setValidators(requiresOAuth2 ? [Validators.required] : []);
    clientIdCtrl.updateValueAndValidity({ emitEvent: false });
  }

  /** Whether the single secret control is currently required — kept as a method so the template's "*" marker
   *  tracks _syncAuthModeValidators' own rule instead of restating it. */
  secretRequired(): boolean {
    return this.webhookForm.controls.authMode.value !== 'none' && !this.reusingExisting();
  }

  /** The label the single secret control carries for the currently selected auth mode. */
  secretLabel(): string {
    switch (this.webhookForm.controls.authMode.value) {
      case 'bearer': return 'Bearer token';
      case 'apiKeyHeader': return 'API key';
      case 'basic': return 'Credentials (username:password)';
      case 'hmacSha256': return 'HMAC shared secret';
      case 'oauth2ClientCredentials': return 'Client secret';
      default: return 'Ingest URL (stored as the secret)';
    }
  }

  isValid(): boolean {
    return this.webhookForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.webhookForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.webhookForm.value;
    return {
      dest_name: v.name ?? '',
      dest_dlwEndpointUrl: v.endpointUrl ?? '',
      dest_dlwAuthMode: v.authMode ?? 'bearer',
      dest_dlwSecret: v.secretValue ?? '',
      dest_dlwAuthHeaderName: v.authHeaderName ?? '',
      dest_dlwSignatureHeaderName: v.signatureHeaderName ?? '',
      dest_dlwTimestampHeaderName: v.timestampHeaderName ?? '',
      dest_dlwTokenEndpoint: v.tokenEndpoint ?? '',
      dest_dlwClientId: v.clientId ?? '',
      dest_dlwScope: v.scope ?? '',
      dest_dlwPayloadShape: v.payloadShape ?? 'ndjson',
      dest_dlwHttpMethod: v.httpMethod ?? 'POST',
      dest_dlwContentType: v.contentType ?? '',
      dest_dlwCompression: v.compression ?? 'none',
      dest_dlwBatchSize: String(v.batchSize ?? 500),
      dest_dlwMaxRequestBytes: String(v.maxRequestBytes ?? 4194304),
      dest_dlwTimeoutSeconds: String(v.timeoutSeconds ?? 30),
      dest_dlwRetryCount: String(v.retryCount ?? 3),
      dest_dlwRetryBackoffSeconds: String(v.retryBackoffSeconds ?? 2),
      dest_dlwExpectedStatusCodes: v.expectedStatusCodes ?? '',
      dest_dlwHeadersJson: v.headersJson ?? '',
      dest_dlwIncludeSourceJson: String(v.includeSourceJson ?? false),
      dest_dlwOnFailure: v.onFailure ?? 'fail',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'datalake')) as Record<string, string>,
      // Auth mode "none" with an endpoint configured needs no secret; with the endpoint left blank the secret
      // IS the ingest URL (see DataLakeWebhookSettings.EndpointComesFromSecret).
      secret: config['dest_dlwAuthMode'] === 'none'
        ? (config['dest_dlwEndpointUrl'] ? '' : (config['dest_dlwSecret'] || ''))
        : (config['dest_dlwSecret'] || ''),
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.webhookForm.patchValue({
      name: fields['dest_name'] || this.webhookForm.value.name || 'Data Lake Webhook',
      endpointUrl: fields['dest_dlwEndpointUrl'] || target || '',
      authMode: fields['dest_dlwAuthMode'] || 'bearer',
      secretValue: '',
      authHeaderName: fields['dest_dlwAuthHeaderName'] || '',
      signatureHeaderName: fields['dest_dlwSignatureHeaderName'] || '',
      timestampHeaderName: fields['dest_dlwTimestampHeaderName'] || '',
      tokenEndpoint: fields['dest_dlwTokenEndpoint'] || '',
      clientId: fields['dest_dlwClientId'] || '',
      scope: fields['dest_dlwScope'] || '',
      payloadShape: fields['dest_dlwPayloadShape'] || 'ndjson',
      httpMethod: fields['dest_dlwHttpMethod'] || 'POST',
      contentType: fields['dest_dlwContentType'] || '',
      compression: fields['dest_dlwCompression'] || 'none',
      batchSize: Number(fields['dest_dlwBatchSize'] || 500),
      maxRequestBytes: Number(fields['dest_dlwMaxRequestBytes'] || 4194304),
      timeoutSeconds: Number(fields['dest_dlwTimeoutSeconds'] || 30),
      retryCount: Number(fields['dest_dlwRetryCount'] ?? 3),
      retryBackoffSeconds: Number(fields['dest_dlwRetryBackoffSeconds'] ?? 2),
      expectedStatusCodes: fields['dest_dlwExpectedStatusCodes'] || '',
      headersJson: fields['dest_dlwHeadersJson'] || '',
      includeSourceJson: fields['dest_dlwIncludeSourceJson'] === 'true',
      onFailure: fields['dest_dlwOnFailure'] || 'fail',
    });
    this._syncAuthModeValidators(this.webhookForm.value.authMode ?? null, this.reusingExisting());
  }

  reset(): void {
    this.webhookForm.reset({
      name: 'Data Lake Webhook', endpointUrl: '', authMode: 'bearer', secretValue: '',
      authHeaderName: '', signatureHeaderName: '', timestampHeaderName: '',
      tokenEndpoint: '', clientId: '', scope: '',
      payloadShape: 'ndjson', httpMethod: 'POST', contentType: '', compression: 'none',
      batchSize: 500, maxRequestBytes: 4194304, timeoutSeconds: 30, retryCount: 3, retryBackoffSeconds: 2,
      expectedStatusCodes: '', headersJson: '', includeSourceJson: false, onFailure: 'fail',
    });
    this._syncAuthModeValidators(this.webhookForm.value.authMode ?? null, this.reusingExisting());
  }
}
