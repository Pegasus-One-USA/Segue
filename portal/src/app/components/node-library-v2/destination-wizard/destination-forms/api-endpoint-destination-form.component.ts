import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * API Endpoint destination — the general-purpose "bring your own endpoint" REST destination. Unlike the REST API
 * destination (one record per request, no auth beyond a raw secret, no batching, no retry) this exposes every
 * option a real integration needs: HTTP method, eight auth modes, custom headers/query params, batching, gzip,
 * retry with backoff, and a configurable success/failure contract. Also distinct from the Data Lake Webhook
 * destination, which is purpose-built for data-lake ingestion front doors and always assumes PHI/https-only —
 * this one defaults to plain http allowed (toggle "Require HTTPS" for a PHI-carrying integration).
 */
@Component({
  selector: 'app-api-endpoint-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './api-endpoint-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
  styles: [`
    .ae-adv { margin-top: 4px; }
    .ae-adv-toggle {
      display: flex; align-items: center; gap: 8px; width: 100%;
      background: none; border: 0; border-top: 1px solid var(--color-border);
      padding: 14px 0 0; cursor: pointer; text-align: left;
      font: inherit; font-weight: 600; color: var(--color-text);
    }
    .ae-adv-toggle:hover { color: var(--color-primary, #00A89D); }
    .ae-adv-toggle:focus-visible { outline: 2px solid var(--color-primary, #00A89D); outline-offset: 2px; }
    .ae-adv-caret { display: inline-block; transition: transform 120ms ease; color: var(--color-text-muted, #6b7280); }
    .ae-adv-caret--open { transform: rotate(90deg); }
    @media (prefers-reduced-motion: reduce) { .ae-adv-caret { transition: none; } }
    .ae-adv-sub { font-weight: 400; font-size: 12px; color: var(--color-text-muted, #6b7280); }
    .ae-adv-badge {
      font-weight: 600; font-size: 11px; letter-spacing: .04em; text-transform: uppercase;
      color: var(--color-error, #b32020);
    }
  `],
})
export class ApiEndpointDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);

  static readonly ENDPOINT_URL_PATTERN = /^https?:\/\/\S+$/;

  readonly apiForm = this.fb.group({
    name: ['API Endpoint', [Validators.required]],
    endpointUrl: ['', [Validators.required, Validators.pattern(ApiEndpointDestinationFormComponent.ENDPOINT_URL_PATTERN)]],
    httpMethod: ['POST', [Validators.required]],
    authMode: ['none', [Validators.required]],
    // The one secret control for every credential-bearing auth mode (bearer token / API key / "user:password" /
    // HMAC shared secret / OAuth2 client secret / base64 PFX for client certificates) — its label swaps per
    // authMode in the template. Never repopulated when reusing an existing connection.
    secretValue: ['', []],
    authHeaderName: ['', []],
    apiKeyQueryParamName: ['', []],
    signatureHeaderName: ['', []],
    timestampHeaderName: ['', []],
    tokenEndpoint: ['', []],
    clientId: ['', []],
    scope: ['', []],
    payloadShape: ['jsonArray', [Validators.required]],
    contentType: ['', []],
    compression: ['none', []],
    requireHttps: [false, []],
    batchSize: [500, [Validators.required, Validators.min(1), Validators.max(50000)]],
    maxRequestBytes: [4194304, [Validators.required, Validators.min(1024), Validators.max(100663296)]],
    timeoutSeconds: [30, [Validators.required, Validators.min(1), Validators.max(600)]],
    retryCount: [3, [Validators.required, Validators.min(0), Validators.max(10)]],
    retryBackoffSeconds: [2, [Validators.min(0), Validators.max(60)]],
    expectedStatusCodes: ['', []],
    headersJson: ['', [ApiEndpointDestinationFormComponent.jsonObjectValidator]],
    queryParamsJson: ['', [ApiEndpointDestinationFormComponent.jsonObjectValidator]],
    // The caller's own request body shape, with {{fieldName}} placeholders — see ApiEndpointSettings.
    // Deliberately its own (non-JSON-object) validator: the template is any valid JSON document (an object,
    // typically, but not required to be), not specifically a flat key/value map like headersJson/queryParamsJson.
    bodyTemplateJson: ['', [ApiEndpointDestinationFormComponent.jsonValidator]],
    includeSourceJson: [false, []],
    onFailure: ['fail', [Validators.required]],
    // Multi-resource (flat/nested parent-child) combining — opt-in, defaults to 'none' so every destination that
    // doesn't use this behaves exactly as it always has. See ApiEndpointMultiResourceMode/ApiEndpointResourceRelation.
    multiResourceMode: ['none', []],
    resourceRelationsJson: ['', [ApiEndpointDestinationFormComponent.jsonArrayValidator]],
    recordTemplatesByResourceTypeJson: ['', [ApiEndpointDestinationFormComponent.jsonObjectValidator]],
    batchTemplateJson: ['', [ApiEndpointDestinationFormComponent.jsonValidator]],
  });

  /** Blank passes — an empty box means "none configured", not "invalid". */
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

  /** Blank passes. Used for resourceRelationsJson — a JSON array of { resourceType, parentResourceType?,
   *  correlationColumn?, parentKeyColumn?, nestKey } entries, mirroring ApiEndpointResourceRelation. */
  private static jsonArrayValidator(control: { value: unknown }): Record<string, boolean> | null {
    const raw = (control.value ?? '') as string;
    if (!raw.trim()) return null;
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? null : { jsonArray: true };
    } catch {
      return { jsonArray: true };
    }
  }

  /** Blank passes. Unlike jsonObjectValidator above, any valid JSON document is accepted — the request body
   *  template is whatever shape the caller's API expects. */
  private static jsonValidator(control: { value: unknown }): Record<string, boolean> | null {
    const raw = (control.value ?? '') as string;
    if (!raw.trim()) return null;
    try {
      JSON.parse(raw);
      return null;
    } catch {
      return { json: true };
    }
  }

  /** True while the host is reusing a previously-saved connection unchanged — the secret is never repopulated
   *  when patching from an existing connection, so requiring it would block reuse unless the user retyped it. */
  readonly reusingExisting = input<boolean>(false);
  /** Set by DestinationWizardComponent.activeFormInputs() on every registry-routed form (see its own doc
   *  comment) — declaring it here is what stops NG0303 ("Can't set value of the 'existingDestinationId'
   *  input") from throwing on every change-detection pass once this form is mounted. Not otherwise read by
   *  this form (its own Test Connection resolves credentials from the form fields directly, not a stored
   *  secret server-side, unlike the SQL-family forms this pattern originates from). */
  readonly existingDestinationId = input<string | null>(null);

  readonly advancedOpen = signal(false);

  toggleAdvanced(): void {
    this.advancedOpen.update(open => !open);
  }

  /** Called by DestinationWizardComponent.onDestinationTemplateGenerated (duck-typed — see its own doc
   *  comment) after the mapping canvas's "Load JSON payload" on the destination side builds a Request Body
   *  Template with {{ColumnName}} placeholders already matching the columns that same load just created.
   *  Overwrites whatever was here before, same "replaces" semantics as the columns it was built from. */
  setBodyTemplateFromMapping(json: string): void {
    this.apiForm.controls.bodyTemplateJson.setValue(json);
  }

  private static readonly ADVANCED_CONTROLS = [
    'compression', 'maxRequestBytes', 'timeoutSeconds', 'retryCount', 'retryBackoffSeconds', 'contentType',
    'expectedStatusCodes', 'scope', 'signatureHeaderName', 'timestampHeaderName', 'headersJson', 'queryParamsJson',
  ];

  // bodyTemplateJson deliberately lives OUTSIDE the Advanced disclosure (see the template above): unlike every
  // other control in there, it can be the reason someone chose this destination type at all — burying it would
  // defeat the point of "load your API's own JSON shape" being the headline feature, not a footnote.

  hasAdvancedError(): boolean {
    return ApiEndpointDestinationFormComponent.ADVANCED_CONTROLS.some(name => {
      const control = this.apiForm.get(name);
      return !!control && control.invalid && control.touched;
    });
  }

  constructor() {
    effect(() => {
      const authMode = this.apiForm.controls.authMode.value;
      const reusing = this.reusingExisting();
      untracked(() => this._syncAuthModeValidators(authMode, reusing));
    });
    this.apiForm.controls.authMode.valueChanges.subscribe(v =>
      this._syncAuthModeValidators(v, this.reusingExisting()));
  }

  /** Mirrors the backend's ApiEndpointSettings.Parse rules: API-key-header mode needs a header name, OAuth2 needs
   *  a token endpoint and client id, and every mode except "none" needs credential material — but only for a
   *  genuinely new connection, since reusingExisting never repopulates the secret. */
  private _syncAuthModeValidators(authMode: string | null, reusingExisting: boolean): void {
    const secretCtrl = this.apiForm.get('secretValue')!;
    secretCtrl.setValidators(authMode !== 'none' && !reusingExisting ? [Validators.required] : []);
    secretCtrl.updateValueAndValidity({ emitEvent: false });

    const headerNameCtrl = this.apiForm.get('authHeaderName')!;
    headerNameCtrl.setValidators(authMode === 'apiKeyHeader' ? [Validators.required] : []);
    headerNameCtrl.updateValueAndValidity({ emitEvent: false });

    const requiresOAuth2 = authMode === 'oauth2ClientCredentials';
    const tokenEndpointCtrl = this.apiForm.get('tokenEndpoint')!;
    tokenEndpointCtrl.setValidators(requiresOAuth2 ? [Validators.required] : []);
    tokenEndpointCtrl.updateValueAndValidity({ emitEvent: false });
    const clientIdCtrl = this.apiForm.get('clientId')!;
    clientIdCtrl.setValidators(requiresOAuth2 ? [Validators.required] : []);
    clientIdCtrl.updateValueAndValidity({ emitEvent: false });
  }

  secretRequired(): boolean {
    return this.apiForm.controls.authMode.value !== 'none' && !this.reusingExisting();
  }

  secretLabel(): string {
    switch (this.apiForm.controls.authMode.value) {
      case 'bearer': return 'Bearer token';
      case 'apiKeyHeader': return 'API key';
      case 'apiKeyQuery': return 'API key';
      case 'basic': return 'Credentials (username:password)';
      case 'hmacSha256': return 'HMAC shared secret';
      case 'oauth2ClientCredentials': return 'Client secret';
      case 'clientCertificate': return 'Client certificate (base64 PFX, optionally "|password")';
      default: return 'Credential';
    }
  }

  isValid(): boolean {
    return this.apiForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.apiForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.apiForm.value;
    return {
      dest_name: v.name ?? '',
      dest_apiEndpointUrl: v.endpointUrl ?? '',
      dest_apiHttpMethod: v.httpMethod ?? 'POST',
      dest_apiAuthMode: v.authMode ?? 'none',
      dest_apiSecret: v.secretValue ?? '',
      dest_apiAuthHeaderName: v.authHeaderName ?? '',
      dest_apiKeyQueryParamName: v.apiKeyQueryParamName ?? '',
      dest_apiSignatureHeaderName: v.signatureHeaderName ?? '',
      dest_apiTimestampHeaderName: v.timestampHeaderName ?? '',
      dest_apiTokenEndpoint: v.tokenEndpoint ?? '',
      dest_apiClientId: v.clientId ?? '',
      dest_apiScope: v.scope ?? '',
      dest_apiPayloadShape: v.payloadShape ?? 'jsonArray',
      dest_apiContentType: v.contentType ?? '',
      dest_apiCompression: v.compression ?? 'none',
      dest_apiRequireHttps: String(v.requireHttps ?? false),
      dest_apiBatchSize: String(v.batchSize ?? 500),
      dest_apiMaxRequestBytes: String(v.maxRequestBytes ?? 4194304),
      dest_apiTimeoutSeconds: String(v.timeoutSeconds ?? 30),
      dest_apiRetryCount: String(v.retryCount ?? 3),
      dest_apiRetryBackoffSeconds: String(v.retryBackoffSeconds ?? 2),
      dest_apiExpectedStatusCodes: v.expectedStatusCodes ?? '',
      dest_apiHeadersJson: v.headersJson ?? '',
      dest_apiQueryParamsJson: v.queryParamsJson ?? '',
      dest_apiBodyTemplateJson: v.bodyTemplateJson ?? '',
      dest_apiIncludeSourceJson: String(v.includeSourceJson ?? false),
      dest_apiOnFailure: v.onFailure ?? 'fail',
      dest_apiMultiResourceMode: v.multiResourceMode ?? 'none',
      dest_apiResourceRelationsJson: v.resourceRelationsJson ?? '',
      dest_apiRecordTemplatesByResourceType: v.recordTemplatesByResourceTypeJson ?? '',
      dest_apiBatchTemplateJson: v.batchTemplateJson ?? '',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'apiendpoint')) as Record<string, string>,
      secret: config['dest_apiSecret'] || '',
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.apiForm.patchValue({
      name: fields['dest_name'] || this.apiForm.value.name || 'API Endpoint',
      endpointUrl: fields['dest_apiEndpointUrl'] || target || '',
      httpMethod: fields['dest_apiHttpMethod'] || 'POST',
      authMode: fields['dest_apiAuthMode'] || 'none',
      secretValue: '',
      authHeaderName: fields['dest_apiAuthHeaderName'] || '',
      apiKeyQueryParamName: fields['dest_apiKeyQueryParamName'] || '',
      signatureHeaderName: fields['dest_apiSignatureHeaderName'] || '',
      timestampHeaderName: fields['dest_apiTimestampHeaderName'] || '',
      tokenEndpoint: fields['dest_apiTokenEndpoint'] || '',
      clientId: fields['dest_apiClientId'] || '',
      scope: fields['dest_apiScope'] || '',
      payloadShape: fields['dest_apiPayloadShape'] || 'jsonArray',
      contentType: fields['dest_apiContentType'] || '',
      compression: fields['dest_apiCompression'] || 'none',
      requireHttps: fields['dest_apiRequireHttps'] === 'true',
      batchSize: Number(fields['dest_apiBatchSize'] || 500),
      maxRequestBytes: Number(fields['dest_apiMaxRequestBytes'] || 4194304),
      timeoutSeconds: Number(fields['dest_apiTimeoutSeconds'] || 30),
      retryCount: Number(fields['dest_apiRetryCount'] ?? 3),
      retryBackoffSeconds: Number(fields['dest_apiRetryBackoffSeconds'] ?? 2),
      expectedStatusCodes: fields['dest_apiExpectedStatusCodes'] || '',
      headersJson: fields['dest_apiHeadersJson'] || '',
      queryParamsJson: fields['dest_apiQueryParamsJson'] || '',
      bodyTemplateJson: fields['dest_apiBodyTemplateJson'] || '',
      includeSourceJson: fields['dest_apiIncludeSourceJson'] === 'true',
      onFailure: fields['dest_apiOnFailure'] || 'fail',
      multiResourceMode: fields['dest_apiMultiResourceMode'] || 'none',
      resourceRelationsJson: fields['dest_apiResourceRelationsJson'] || '',
      recordTemplatesByResourceTypeJson: fields['dest_apiRecordTemplatesByResourceType'] || '',
      batchTemplateJson: fields['dest_apiBatchTemplateJson'] || '',
    });
    this._syncAuthModeValidators(this.apiForm.value.authMode ?? null, this.reusingExisting());
  }

  reset(): void {
    this.apiForm.reset({
      name: 'API Endpoint', endpointUrl: '', httpMethod: 'POST', authMode: 'none', secretValue: '',
      authHeaderName: '', apiKeyQueryParamName: '', signatureHeaderName: '', timestampHeaderName: '',
      tokenEndpoint: '', clientId: '', scope: '',
      payloadShape: 'jsonArray', contentType: '', compression: 'none', requireHttps: false,
      batchSize: 500, maxRequestBytes: 4194304, timeoutSeconds: 30, retryCount: 3, retryBackoffSeconds: 2,
      expectedStatusCodes: '', headersJson: '', queryParamsJson: '', bodyTemplateJson: '',
      includeSourceJson: false, onFailure: 'fail',
      multiResourceMode: 'none', resourceRelationsJson: '', recordTemplatesByResourceTypeJson: '', batchTemplateJson: '',
    });
    this._syncAuthModeValidators(this.apiForm.value.authMode ?? null, this.reusingExisting());
  }
}
