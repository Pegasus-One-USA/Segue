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
    .ae-relations-builder { display: flex; flex-direction: column; gap: 8px; }
    .ae-relation-row {
      display: flex; align-items: center; gap: 6px; flex-wrap: wrap;
      background: var(--color-surface-subtle, #f8fafc); border: 1px solid var(--color-border);
      border-radius: 8px; padding: 8px;
    }
    .ae-relation-row .dw-input, .ae-relation-row .dw-select { flex: 1 1 140px; min-width: 0; }
    .ae-relation-row-label { font-size: 11px; font-weight: 600; color: var(--color-text-muted, #6b7280); flex: 0 0 auto; white-space: nowrap; }
    .ae-relation-remove {
      flex: 0 0 auto; width: 24px; height: 24px; border-radius: 999px; border: none; cursor: pointer;
      background: var(--color-error-soft, #fee2e2); color: var(--color-error, #b32020);
      font-size: 12px; font-weight: 700; display: grid; place-items: center;
    }
    .ae-relation-remove:hover { opacity: 0.85; }
    .ae-relation-add {
      align-self: flex-start; background: none; border: 1px dashed var(--color-border);
      border-radius: 8px; padding: 6px 12px; font-size: 12.5px; font-weight: 600;
      color: var(--color-primary, #00A89D); cursor: pointer;
    }
    .ae-relation-add:hover { background: var(--color-primary-soft, #e6f7f5); }
    .ae-templates-summary { font-size: 12.5px; margin: 4px 0 8px; }
    .ae-templates-pill {
      display: inline-block; font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 999px;
      background: var(--color-primary-soft, #e6f7f5); color: var(--color-primary-dark, #007a72);
      margin: 2px 4px 2px 0;
    }
    .ae-templates-empty { color: var(--color-text-muted, #6b7280); font-style: italic; }
    details.ae-raw-json { margin-top: 4px; }
    details.ae-raw-json summary { cursor: pointer; font-size: 12px; font-weight: 600; color: var(--color-text-muted, #6b7280); }
    details.ae-raw-json summary:hover { color: var(--color-primary, #00A89D); }
    details.ae-raw-json .dw-input { margin-top: 8px; }
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
   *  Overwrites whatever was here before, same "replaces" semantics as the columns it was built from. Only
   *  reached for a single-resource destination — see isMultiResourceModeActive/setRecordTemplateForResource
   *  below for the multi-resource routing this same event takes instead. */
  setBodyTemplateFromMapping(json: string): void {
    this.apiForm.controls.bodyTemplateJson.setValue(json);
  }

  /** Tells DestinationWizardComponent.onDestinationTemplateGenerated which of the two template fields a
   *  "Load JSON payload" load should land in. */
  isMultiResourceModeActive(): boolean {
    return this.apiForm.controls.multiResourceMode.value !== 'none';
  }

  /** Multi-resource counterpart to setBodyTemplateFromMapping — every participating resource type needs its
   *  OWN Request Body Template (dest_apiBodyTemplateJson is never read once multi-resource is on), so this
   *  merges just THIS resource's freshly-built template into dest_apiRecordTemplatesByResourceType, keyed by
   *  resource type, leaving every other already-configured resource type's own template untouched — the same
   *  "only touch what this load actually affected" contract setBodyTemplateFromMapping already has for the
   *  single-resource case, just scoped to one entry in a dictionary instead of the whole field. Malformed
   *  existing JSON is treated as empty rather than failing the load, matching every other tolerant-parse
   *  convention this form already follows (see jsonObjectValidator). */
  setRecordTemplateForResource(resource: string, json: string): void {
    const raw = this.apiForm.controls.recordTemplatesByResourceTypeJson.value ?? '';
    let existing: Record<string, string> = {};
    if (raw.trim()) {
      try {
        const parsed = JSON.parse(raw);
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          existing = parsed as Record<string, string>;
        }
      } catch {
        // Malformed — treated as empty, same tolerance as everywhere else this field is parsed.
      }
    }
    existing[resource] = json;
    this.apiForm.controls.recordTemplatesByResourceTypeJson.setValue(JSON.stringify(existing));
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
    // Flat <-> Nested changes which fields _syncRelationRowsToControl serializes (parentResourceType/
    // correlationColumn/parentKeyColumn only apply to Nested) — re-sync so switching modes updates
    // dest_apiResourceRelationsJson immediately rather than waiting for the next row edit.
    this.apiForm.controls.multiResourceMode.valueChanges.subscribe(() => this._syncRelationRowsToControl());
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
    this._parseRelationRowsFromControl();
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
    this.relationRows.set([]);
  }

  // ── multi-resource "Participating resource types" builder ─────────────────────────────────────
  // A friendlier alternative to hand-typing dest_apiResourceRelationsJson's raw JSON array syntax
  // (escaped quotes, exact key names) — a row per resource type, kept in sync with the SAME
  // resourceRelationsJson FormControl getFullConfig()/validation already use, so nothing downstream
  // needs to know this builder exists. The raw textarea stays available (collapsed, see the template)
  // for anyone who already has JSON to paste or needs something the builder doesn't expose.
  readonly relationRows = signal<ResourceRelationRow[]>([]);

  addRelationRow(): void {
    this.relationRows.update(rows => [
      ...rows,
      { resourceType: '', nestKey: '', parentResourceType: '', correlationColumn: '', parentKeyColumn: '' },
    ]);
    this._syncRelationRowsToControl();
  }

  removeRelationRow(index: number): void {
    this.relationRows.update(rows => rows.filter((_, i) => i !== index));
    this._syncRelationRowsToControl();
  }

  updateRelationRow(index: number, patch: Partial<ResourceRelationRow>): void {
    this.relationRows.update(rows => rows.map((r, i) => (i === index ? { ...r, ...patch } : r)));
    this._syncRelationRowsToControl();
  }

  /** Every other row's resourceType — the candidate list for "Parent resource" (a row can't be its own
   *  parent, and a blank resourceType isn't a usable parent yet). */
  parentCandidatesFor(index: number): string[] {
    return this.relationRows()
      .filter((r, i) => i !== index && r.resourceType.trim())
      .map(r => r.resourceType.trim());
  }

  /** Lowercase-plural default so a row with an empty Nest key still produces valid JSON — "Patient" ->
   *  "patients", "Encounter" -> "encounters". Only applied at serialization time; the input itself stays
   *  blank until the user types something, so it's obvious this is a placeholder default, not a real value. */
  private _defaultNestKey(resourceType: string): string {
    const t = resourceType.trim();
    if (!t) return '';
    const lower = t.charAt(0).toLowerCase() + t.slice(1);
    return lower.endsWith('s') ? lower : `${lower}s`;
  }

  private _syncRelationRowsToControl(): void {
    const isNested = this.apiForm.controls.multiResourceMode.value === 'nested';
    const entries = this.relationRows()
      .filter(r => r.resourceType.trim())
      .map(r => {
        const entry: Record<string, string> = {
          resourceType: r.resourceType.trim(),
          nestKey: r.nestKey.trim() || this._defaultNestKey(r.resourceType),
        };
        if (isNested && r.parentResourceType.trim()) {
          entry['parentResourceType'] = r.parentResourceType.trim();
          entry['correlationColumn'] = r.correlationColumn.trim();
          entry['parentKeyColumn'] = r.parentKeyColumn.trim();
        }
        return entry;
      });
    this.apiForm.controls.resourceRelationsJson.setValue(entries.length ? JSON.stringify(entries) : '');
  }

  /** Reverse of _syncRelationRowsToControl — reached from patchFrom (reopening a saved destination) so the
   *  builder shows real rows instead of starting empty even though resourceRelationsJson already has content.
   *  Malformed/non-array JSON leaves the builder empty rather than throwing — same tolerance every other
   *  parse in this form already has; the raw textarea still shows the actual saved value either way. */
  private _parseRelationRowsFromControl(): void {
    const raw = this.apiForm.controls.resourceRelationsJson.value ?? '';
    if (!raw.trim()) {
      this.relationRows.set([]);
      return;
    }
    try {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) {
        this.relationRows.set(parsed.map((e: Record<string, unknown>) => ({
          resourceType: typeof e?.['resourceType'] === 'string' ? e['resourceType'] : '',
          nestKey: typeof e?.['nestKey'] === 'string' ? e['nestKey'] : '',
          parentResourceType: typeof e?.['parentResourceType'] === 'string' ? e['parentResourceType'] : '',
          correlationColumn: typeof e?.['correlationColumn'] === 'string' ? e['correlationColumn'] : '',
          parentKeyColumn: typeof e?.['parentKeyColumn'] === 'string' ? e['parentKeyColumn'] : '',
        })));
      }
    } catch {
      // Malformed — builder starts empty; the raw textarea below still shows what's actually saved.
    }
  }

  /** Resource types that currently have their own entry in dest_apiRecordTemplatesByResourceType — shown
   *  as a quick-glance pill summary above the raw textarea so "did my Load JSON payload actually save?" is
   *  answerable without reading escaped JSON. Malformed JSON reads as "none configured", not an error. */
  configuredRecordTemplateResourceTypes(): string[] {
    const raw = this.apiForm.controls.recordTemplatesByResourceTypeJson.value ?? '';
    if (!raw.trim()) return [];
    try {
      const parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? Object.keys(parsed) : [];
    } catch {
      return [];
    }
  }
}

interface ResourceRelationRow {
  resourceType: string;
  nestKey: string;
  parentResourceType: string;
  correlationColumn: string;
  parentKeyColumn: string;
}
