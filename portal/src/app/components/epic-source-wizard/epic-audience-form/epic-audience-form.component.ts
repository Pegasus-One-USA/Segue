import {
  Component, output, inject, signal, computed, OnInit, DestroyRef, ElementRef, ViewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule, FormBuilder, Validators, ValidatorFn, AbstractControl, ValidationErrors } from '@angular/forms';
import { WizardService } from '../../../services/wizard.service';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { ToastService } from '../../../services/toast.service';
import { EPIC_ENV } from '../../../data/epic-environments.data';
import { EnvKey } from '../../../models/epic-env.model';
import { AppKey } from '../../../models/epic-app.model';
import { FullDiscoveredValues } from '../models/epic-config.model';

export type EpicAudience = 'provider-ehr-launch' | 'provider-standalone' | 'backend-system' | 'patient';

const FHIR_RESOURCES = [
  'Patient', 'Encounter', 'Observation', 'Condition', 'MedicationRequest',
  'AllergyIntolerance', 'Immunization', 'Procedure', 'DiagnosticReport',
  'DocumentReference', 'Practitioner', 'PractitionerRole',
];

function urlValidator(ctrl: AbstractControl): ValidationErrors | null {
  if (!ctrl.value) return null;
  try { new URL(ctrl.value); return null; } catch { return { url: true }; }
}

/**
 * Determines the SMART scope version a source uses. Prefers the explicit permission-v1/permission-v2 capability
 * tokens; when absent (Epic frequently omits them) it infers from scopes_supported — a granular v2 suffix like
 * `.rs` / `.cruds` implies v2, coarse `.read` / `.write` implies v1. Returns null when nothing is conclusive.
 */
// True when an advertised scope (possibly with '*' wildcards in the resource/action segment) covers a concrete scope —
// e.g. advertised "user/*.rs" covers "user/Patient.rs". Mirrors the backend ScopeGeneratorService matcher.
function scopeWildcardCovers(advertised: string, scope: string): boolean {
  const split = (s: string): [string, string, string] => {
    const slash = s.indexOf('/');
    if (slash < 0) return [s, '', ''];
    const prefix = s.slice(0, slash);
    const rest = s.slice(slash + 1);
    const dot = rest.lastIndexOf('.');
    return dot < 0 ? [prefix, rest, ''] : [prefix, rest.slice(0, dot), rest.slice(dot + 1)];
  };
  const [ap, ar, aa] = split(advertised);
  const [sp, sr, sa] = split(scope);
  return ap.toLowerCase() === sp.toLowerCase()
    && (ar === '*' || ar.toLowerCase() === sr.toLowerCase())
    && (aa === '*' || aa.toLowerCase() === sa.toLowerCase());
}

/**
 * `token_endpoint_auth_methods_supported` is a server-wide list (every method the FHIR server accepts from any
 * client), not a statement about how *this* app is registered — Epic's discovery document lists
 * client_secret_basic/post and private_key_jwt for essentially every environment regardless of whether a given app
 * is public or confidential. So it can only drive an auto-selection where the SMART flow itself mandates one method:
 * Backend Services (system/backend-system) is JWT-only per spec, so that's the one case we can safely auto-select.
 * Interactive audiences (EHR launch / standalone / patient) are ordinarily public + PKCE and the actual choice
 * depends on how the customer registered their app in Epic — something no discovery document can reveal — so we
 * leave the user's selection alone there rather than force it toward whatever the server merely *can* accept.
 */
function detectAuthMethod(audience: EpicAudience, authMethodsSupported: string[]): 'public' | 'secret' | 'jwt' | null {
  if (audience !== 'backend-system') return null;
  const methods = authMethodsSupported.map(m => m.toLowerCase());
  return methods.includes('private_key_jwt') ? 'jwt' : null;
}

function detectScopeVersion(capabilities: string[], scopesSupported: string[]): 'v1' | 'v2' | null {
  if (capabilities.includes('permission-v2')) return 'v2';
  if (capabilities.includes('permission-v1')) return 'v1';
  const suffix = (s: string): string => (s.includes('.') ? s.slice(s.lastIndexOf('.') + 1) : '').toLowerCase();
  if (scopesSupported.some(s => /^[cruds]+$/.test(suffix(s)))) return 'v2';
  if (scopesSupported.some(s => suffix(s) === 'read' || suffix(s) === 'write')) return 'v1';
  return null;
}

// ── Per-audience field visibility/requirement registry ─────────────────────────
// Adding a new audience means adding one entry here — no template/validator edits.
// All four audiences now show a connection form; only the redirect/launch/retrieval
// shape differs between them.
interface AudienceFieldConfig {
  showLaunchUrl: boolean;
  showRedirect: boolean;
  showCdsHooks: boolean;
  /** Whether the retrieval-method section applies (Backend System only). */
  showRetrieval: boolean;
  /** Whether the shared Resource Type picker (Section 5) applies. False for Backend System,
   *  where Resource Type instead lives inside the selected retrieval method's own config —
   *  never both, to avoid showing two Resource Type pickers at once. */
  showResourcePicker: boolean;
  /** 'readonly' = auto-populated Redirect URI (providers); 'editable' = mandatory Callback URL (patient). */
  redirectMode: 'readonly' | 'editable';
  redirectLabel: string;
  scopePrefix: 'user' | 'patient' | 'system';
  /** Interactive audiences add openid/fhirUser/offline_access/launch to the scope string; Backend System does not. */
  includeInteractiveScopes: boolean;
}

const AUDIENCE_FIELD_CONFIG: Record<EpicAudience, AudienceFieldConfig> = {
  // CDS Hooks removed from the UI (not required) — flag kept for future use but disabled everywhere.
  'provider-ehr-launch': { showLaunchUrl: true,  showRedirect: true,  showCdsHooks: false, showRetrieval: false, showResourcePicker: true,  redirectMode: 'editable', redirectLabel: 'Redirect URI', scopePrefix: 'user',    includeInteractiveScopes: true },
  'provider-standalone': { showLaunchUrl: true,  showRedirect: true,  showCdsHooks: false, showRetrieval: false, showResourcePicker: true,  redirectMode: 'editable', redirectLabel: 'Redirect URI', scopePrefix: 'user',    includeInteractiveScopes: true },
  'patient':             { showLaunchUrl: false, showRedirect: true,  showCdsHooks: false, showRetrieval: false, showResourcePicker: true,  redirectMode: 'editable', redirectLabel: 'Callback URL', scopePrefix: 'patient', includeInteractiveScopes: true },
  'backend-system':      { showLaunchUrl: false, showRedirect: false, showCdsHooks: false, showRetrieval: true,  showResourcePicker: false, redirectMode: 'readonly', redirectLabel: '',             scopePrefix: 'system',  includeInteractiveScopes: false },
};

// ── Data Retrieval Method registry (Backend System only) ───────────────────────
// Adding a new method means adding one entry here — the dropdown, the field list,
// validators, and clearing-on-switch all read from this map, never a template `@if`.
export type RetrievalMethod = 'subscription' | 'webhook' | 'search-rest' | 'bulk-export';

// Each retrieval method owns its own Resource Type control (never shared) so that
// switching methods never shows two Resource Type pickers, or leaks one method's
// selection into another's.
type RetrievalFieldKey =
  | 'subscriptionResourceType' | 'webhookResourceType' | 'searchRestResourceType' | 'bulkExportResourceType'
  | 'eventType' | 'notificationPayload' | 'endpointType' | 'reconciliationSchedule'
  | 'payloadFormat' | 'searchCriteria' | 'incrementalCursor' | 'schedulePollFrequency' | 'runMode'
  | 'exportScope' | 'groupId' | 'patientIdList' | 'fhirOutputFormat';

const RETRIEVAL_FIELD_KEYS: readonly RetrievalFieldKey[] = [
  'subscriptionResourceType', 'webhookResourceType', 'searchRestResourceType', 'bulkExportResourceType',
  'eventType', 'notificationPayload', 'endpointType', 'reconciliationSchedule',
  'payloadFormat', 'searchCriteria', 'incrementalCursor', 'schedulePollFrequency', 'runMode',
  'exportScope', 'groupId', 'patientIdList', 'fhirOutputFormat',
];

interface RetrievalFieldOption { value: string; label: string; }

interface RetrievalFieldDef {
  key: RetrievalFieldKey;
  label: string;
  type: 'select' | 'text' | 'textarea' | 'checkbox' | 'multiselect';
  required: boolean;
  options?: readonly RetrievalFieldOption[];
  placeholder?: string;
  hint?: string;
  /** Bulk Export only: Group ID / Patient ID appear only for the matching Export Scope. */
  visibleWhen?: (exportScope: string) => boolean;
}

interface RetrievalMethodConfig {
  value: RetrievalMethod;
  label: string;
  description: string;
  fields: readonly RetrievalFieldDef[];
}

const POLL_FREQUENCY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '5m',  label: 'Every 5 minutes' },
  { value: '15m', label: 'Every 15 minutes' },
  { value: '30m', label: 'Every 30 minutes' },
  { value: '1h',  label: 'Hourly' },
  { value: '1d',  label: 'Daily' },
];

const RECONCILIATION_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'none', label: 'Disabled' },
  { value: '1h',   label: 'Hourly' },
  { value: '6h',   label: 'Every 6 hours' },
  { value: '1d',   label: 'Daily' },
  { value: '1w',   label: 'Weekly' },
];

const ENDPOINT_TYPE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'rest-hook', label: 'REST Hook (HTTPS callback)' },
  { value: 'websocket', label: 'WebSocket' },
  { value: 'mllp',      label: 'MLLP (HL7 v2)' },
];

const EVENT_TYPE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'created',            label: 'Record created' },
  { value: 'updated',            label: 'Record updated' },
  { value: 'created-or-updated', label: 'Created or updated' },
  { value: 'deleted',            label: 'Record deleted' },
];

const RETRIEVAL_METHOD_CONFIG: Record<RetrievalMethod, RetrievalMethodConfig> = {
  subscription: {
    value: 'subscription',
    label: 'Subscription',
    description: 'Epic pushes change notifications through a FHIR Subscription.',
    fields: [
      { key: 'subscriptionResourceType', label: 'Resource Type',        type: 'multiselect', required: true },
      { key: 'eventType',              label: 'Event Type in Epic',      type: 'select',       required: true, options: EVENT_TYPE_OPTIONS },
      { key: 'notificationPayload',    label: 'Notification Payload',    type: 'select',       required: true, options: [
        { value: 'id-only', label: 'ID only' },
        { value: 'full',    label: 'Full resource' },
        { value: 'empty',   label: 'Empty (ping only)' },
      ] },
      { key: 'endpointType',           label: 'Endpoint Type',           type: 'select',       required: true, options: ENDPOINT_TYPE_OPTIONS },
      { key: 'reconciliationSchedule', label: 'Reconciliation Schedule', type: 'select',       required: false, options: RECONCILIATION_OPTIONS, hint: 'Periodic full sync to catch any notifications Epic failed to deliver.' },
    ],
  },
  webhook: {
    value: 'webhook',
    label: 'Webhook',
    description: 'Epic (or a middleware relay) posts updates to an FHIRBridge callback endpoint.',
    fields: [
      { key: 'webhookResourceType',    label: 'Resource Type',           type: 'multiselect', required: true },
      { key: 'eventType',              label: 'Event Type',              type: 'select',       required: true, options: EVENT_TYPE_OPTIONS },
      { key: 'endpointType',           label: 'Endpoint Type',           type: 'select',       required: true, options: ENDPOINT_TYPE_OPTIONS },
      { key: 'payloadFormat',          label: 'Payload Format',          type: 'select',       required: true, options: [
        { value: 'fhir-json', label: 'FHIR JSON' },
        { value: 'fhir-xml',  label: 'FHIR XML' },
      ] },
      { key: 'reconciliationSchedule', label: 'Reconciliation Schedule', type: 'select',       required: false, options: RECONCILIATION_OPTIONS, hint: 'Periodic full sync to catch any callbacks that never arrived.' },
    ],
  },
  'search-rest': {
    value: 'search-rest',
    label: 'Search (REST)',
    description: 'FHIRBridge polls Epic’s FHIR REST API on a schedule.',
    fields: [
      { key: 'searchRestResourceType', label: 'Resource Type',                   type: 'multiselect', required: true },
      { key: 'searchCriteria',        label: 'Search Criteria',                  type: 'text',         required: false, placeholder: 'status=active&category=vital-signs', hint: 'Optional FHIR search parameters appended to every request.' },
      { key: 'incrementalCursor',     label: 'Incremental Cursor (_lastUpdated)', type: 'checkbox',    required: false, hint: 'Only fetch resources changed since the last successful run.' },
      { key: 'schedulePollFrequency', label: 'Schedule / Poll Frequency',        type: 'select',       required: true, options: POLL_FREQUENCY_OPTIONS },
      { key: 'runMode',               label: 'Run Mode',                         type: 'select',       required: true, options: [
        { value: 'incremental', label: 'Incremental sync' },
        { value: 'full',        label: 'Full refresh' },
        { value: 'manual',      label: 'Manual trigger only' },
      ] },
    ],
  },
  'bulk-export': {
    value: 'bulk-export',
    label: 'Bulk Export',
    description: 'Kicks off a FHIR Bulk Data $export job and retrieves the resulting NDJSON files.',
    fields: [
      { key: 'bulkExportResourceType', label: 'Resource Type',               type: 'multiselect', required: true },
      { key: 'exportScope',           label: 'Export Scope',                 type: 'select',       required: true, options: [
        { value: 'system',  label: 'System ($export)' },
        { value: 'group',   label: 'Group ($export)' },
        { value: 'patient', label: 'Patient ($export)' },
      ] },
      { key: 'groupId',               label: 'Group ID',                     type: 'text',         required: true, placeholder: 'e.g. 4diBHMQR-nurOSMS8UbGqQB', hint: 'Epic Group FHIR ID to export.', visibleWhen: scope => scope === 'group' },
      { key: 'patientIdList',         label: 'Patient ID / Patient List',    type: 'textarea',     required: true, placeholder: 'Comma-separated Patient FHIR IDs', hint: 'One or more Patient FHIR IDs to export.', visibleWhen: scope => scope === 'patient' },
      { key: 'incrementalCursor',     label: 'Incremental Cursor (_since)',  type: 'checkbox',     required: false, hint: 'Only export resources changed since the last successful export.' },
      { key: 'fhirOutputFormat',      label: 'FHIR Output Format',           type: 'select',       required: true, options: [
        { value: 'ndjson',      label: 'NDJSON (application/fhir+ndjson)' },
        { value: 'ndjson-gzip', label: 'NDJSON (gzip compressed)' },
      ] },
      { key: 'schedulePollFrequency', label: 'Schedule / Poll Frequency',    type: 'select',       required: true, options: POLL_FREQUENCY_OPTIONS },
    ],
  },
};

@Component({
  selector: 'app-epic-audience-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './epic-audience-form.component.html',
  styleUrl: './epic-audience-form.component.scss',
})
export class EpicAudienceFormComponent implements OnInit {
  readonly cancelled = output<void>();
  readonly saved     = output<void>();

  @ViewChild('formRoot') private readonly formRoot?: ElementRef<HTMLElement>;

  protected readonly wiz       = inject(WizardService);
  private  readonly discovery  = inject(EpicDiscoveryService);
  private  readonly toast      = inject(ToastService);
  private  readonly fb         = inject(FormBuilder);
  private  readonly destroyRef = inject(DestroyRef);

  // Resource Type list: auto-detected from the source's /metadata after Discover; falls back to the static list.
  protected readonly discoveredResourceTypes = signal<string[]>([]);
  protected get resources(): string[] {
    return this.discoveredResourceTypes().length ? this.discoveredResourceTypes() : FHIR_RESOURCES;
  }
  protected get resourcesAreAuto(): boolean { return this.discoveredResourceTypes().length > 0; }

  /** Editing an existing source: resource types are locked (identity-defining) — shown prepopulated but disabled. */
  protected get isEditing(): boolean { return this.wiz.isEditing(); }

  protected readonly discStatus   = signal<'idle' | 'loading' | 'done' | 'error'>('idle');
  protected readonly discValues   = signal<FullDiscoveredValues | null>(null);
  protected readonly discoveredScopes = signal<string[]>([]);
  // True once discovery actually determined the SMART scope version (vs. leaving the default) — drives the badge.
  protected readonly scopeVersionAuto = signal(false);
  // True once discovery actually determined the Client Auth Method (vs. leaving the default) — drives the badge.
  protected readonly authMethodAuto = signal(false);
  protected readonly testStatus   = signal<'idle' | 'running' | 'ok' | 'fail'>('idle');

  protected readonly form = this.fb.nonNullable.group({
    audience:          ['provider-ehr-launch' as EpicAudience, Validators.required],
    environment:       ['sandbox', Validators.required],
    epicBaseUrl:       ['https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4', [Validators.required, urlValidator]],
    tokenEndpoint:     ['', urlValidator],
    authzEndpoint:     ['', urlValidator],
    clientId:          ['', Validators.required],
    // Public + PKCE is the default for interactive apps (EHR launch / standalone / patient) → no client secret needed.
    authMethod:        ['public'],
    clientSecret:      [''],
    jwksUrl:           ['', urlValidator],
    privateKeyRef:     [''],
    launchUrl:         ['https://fhirbridge.com/launch', urlValidator],
    callbackUrl:       ['https://fhirbridge.com/oauth/callback', [Validators.required, urlValidator]],
    resources:         [[] as string[], Validators.required],
    scopeVersion:      ['v2'],
    appName:           ['FHIRBridge Epic'],
    // ── CDS Hooks ──────────────────────────────────────────────────────────────
    cdsDiscoveryUrl:     [''],
    cdsServiceEndpoint:  [''],
    cdsTriggerHook:      [''],
    cdsReturnCard:       [''],
    cdsDtrQuestionnaire: [''],
    // ── Data Retrieval Method (Backend System only) ─────────────────────────────
    retrievalMethod:          ['' as RetrievalMethod | ''],
    subscriptionResourceType: [[] as string[]],
    webhookResourceType:      [[] as string[]],
    searchRestResourceType:   [[] as string[]],
    bulkExportResourceType:   [[] as string[]],
    eventType:              [''],
    notificationPayload:    [''],
    endpointType:           [''],
    reconciliationSchedule: [''],
    payloadFormat:          [''],
    searchCriteria:         [''],
    incrementalCursor:      [false],
    schedulePollFrequency:  [''],
    runMode:                [''],
    exportScope:            [''],
    groupId:                [''],
    patientIdList:          [''],
    fhirOutputFormat:       ['ndjson'],
  });

  // ── reactive bridges from RxJS FormControl.valueChanges → Signals ───────────
  // computed() only re-runs when a *signal* it reads changes; FormControl.value
  // is a plain property, so every computed below must read one of these instead
  // of `.value` directly, or it would freeze at whatever the initial value was.
  private readonly audienceValue        = toSignal(this.form.controls.audience.valueChanges,        { initialValue: this.form.controls.audience.value });
  private readonly authMethodValue      = toSignal(this.form.controls.authMethod.valueChanges,      { initialValue: this.form.controls.authMethod.value });
  private readonly environmentValue     = toSignal(this.form.controls.environment.valueChanges,     { initialValue: this.form.controls.environment.value });
  private readonly resourcesValue       = toSignal(this.form.controls.resources.valueChanges,       { initialValue: this.form.controls.resources.value });
  private readonly retrievalMethodValue = toSignal(this.form.controls.retrievalMethod.valueChanges, { initialValue: this.form.controls.retrievalMethod.value });
  private readonly exportScopeValue     = toSignal(this.form.controls.exportScope.valueChanges,     { initialValue: this.form.controls.exportScope.value });
  private readonly scopeVersionValue    = toSignal(this.form.controls.scopeVersion.valueChanges,    { initialValue: this.form.controls.scopeVersion.value });

  // One bridge per retrieval method's own Resource Type control — never shared,
  // so each method keeps an independent selection instead of leaking into the others.
  private readonly subscriptionResourceTypeValue = toSignal(this.form.controls.subscriptionResourceType.valueChanges, { initialValue: this.form.controls.subscriptionResourceType.value });
  private readonly webhookResourceTypeValue      = toSignal(this.form.controls.webhookResourceType.valueChanges,      { initialValue: this.form.controls.webhookResourceType.value });
  private readonly searchRestResourceTypeValue   = toSignal(this.form.controls.searchRestResourceType.valueChanges,   { initialValue: this.form.controls.searchRestResourceType.value });
  private readonly bulkExportResourceTypeValue   = toSignal(this.form.controls.bulkExportResourceType.valueChanges,   { initialValue: this.form.controls.bulkExportResourceType.value });

  protected readonly audience   = computed(() => this.audienceValue() as EpicAudience);
  protected readonly authMethod = computed(() => this.authMethodValue());

  protected readonly audienceConfig = computed(() => AUDIENCE_FIELD_CONFIG[this.audience()]);
  protected readonly showSecret     = computed(() => this.authMethod() === 'secret');
  protected readonly showJwt        = computed(() => this.authMethod() === 'jwt');

  // ── Data Retrieval Method (Backend System only) ─────────────────────────────
  protected readonly retrievalMethodOptions = Object.values(RETRIEVAL_METHOD_CONFIG)
    .map(c => ({ value: c.value, label: c.label }));

  protected readonly retrievalMethod = computed(() => this.retrievalMethodValue() as RetrievalMethod | '');
  protected readonly retrievalConfig = computed(() => {
    const method = this.retrievalMethod();
    return method ? RETRIEVAL_METHOD_CONFIG[method] : null;
  });

  protected readonly visibleRetrievalFields = computed(() => {
    const cfg = this.retrievalConfig();
    if (!cfg) return [];
    const scope = this.exportScopeValue();
    return cfg.fields.filter(f => !f.visibleWhen || f.visibleWhen(scope));
  });

  /** Generic reader for whichever method's Resource Type control the template is currently rendering. */
  protected selectedRetrievalResourceTypes(key: RetrievalFieldKey): string[] {
    switch (key) {
      case 'subscriptionResourceType': return this.subscriptionResourceTypeValue() ?? [];
      case 'webhookResourceType':      return this.webhookResourceTypeValue() ?? [];
      case 'searchRestResourceType':   return this.searchRestResourceTypeValue() ?? [];
      case 'bulkExportResourceType':   return this.bulkExportResourceTypeValue() ?? [];
      default:                         return [];
    }
  }

  /** The active method's own Resource Type selection — feeds the Connection Settings Scopes preview. */
  protected readonly activeRetrievalResourceTypes = computed(() => {
    const field = this.retrievalConfig()?.fields.find(f => f.type === 'multiselect');
    return field ? this.selectedRetrievalResourceTypes(field.key) : [];
  });

  /** Section numbers shift depending on which optional sections the current audience shows. */
  protected readonly sectionNumbers = computed(() => {
    const cfg = this.audienceConfig();
    let n = 5; // 1 Audience/Env · 2 FHIR Base URL · 3 OAuth Endpoints · 4 Credentials · 5 Resource Type & Scopes
    const urls              = (cfg.showLaunchUrl || cfg.showRedirect) ? ++n : null;
    const cds                = cfg.showCdsHooks ? ++n : null;
    const test                = ++n;
    const retrievalMethod    = cfg.showRetrieval ? ++n : null;
    const retrievalConfig    = cfg.showRetrieval ? ++n : null;
    return { urls, cds, test, retrievalMethod, retrievalConfig };
  });

  protected readonly clientIdLabel = computed(() => {
    const env = this.environmentValue();
    if (env === 'production') return 'Client ID — Production';
    if (env === 'non-production') return 'Client ID — Non-Production';
    return 'Client ID — Sandbox';
  });

  protected readonly selectedResources = computed(() => this.resourcesValue() ?? []);

  protected readonly scopeString = computed(() => {
    const aud = this.audience();
    const cfg = this.audienceConfig();
    // Backend System has no shared Resource Type picker — its scopes are derived from
    // whichever Resource Type list is set on the currently selected retrieval method.
    const res = cfg.showResourcePicker ? this.selectedResources() : this.activeRetrievalResourceTypes();
    const fixed = cfg.includeInteractiveScopes
      ? ['openid', 'fhirUser', 'offline_access', aud === 'provider-ehr-launch' ? 'launch' : 'launch/patient']
      : [];
    // v2 = granular per-resource read+search (SMART v2 uses .rs); v1 = coarse per-resource .read.
    const suffix = this.scopeVersionValue() === 'v2' ? 'rs' : 'read';
    return [...fixed, ...res.map(r => `${cfg.scopePrefix}/${r}.${suffix}`)].join('\n');
  });

  /** True once Discover has fetched the endpoint's advertised scopes — lets the panel say "validated against Epic". */
  protected get scopesValidatedByDiscovery(): boolean { return this.discoveredScopes().length > 0; }

  /**
   * Resource scopes the generated set requests that the source did NOT advertise in its SMART discovery document —
   * mirrors the backend ScopeGeneratorService validation (exact or wildcard match). Base scopes (openid/launch/…) are
   * not validated because servers rarely enumerate them in scopes_supported. Empty until Discover has run.
   */
  protected readonly unsupportedScopes = computed(() => {
    const advertised = this.discoveredScopes();
    if (advertised.length === 0) {
      return [] as string[];
    }
    return this.scopeString()
      .split(/\s+/)
      .filter(Boolean)
      .filter(s => /^[^/]+\/[^.]+\.[^.]+$/.test(s)) // resource-shaped scopes only
      .filter(s => !advertised.some(a => a === s || scopeWildcardCovers(a, s)));
  });

  ngOnInit(): void {
    if (this.wiz.isEditing()) {
      // Pre-populate all fields from saved node data
      this.form.controls.audience.setValue(this.wiz.epicAudience() as EpicAudience);
      this.form.controls.environment.setValue(this.wiz.env());
      this.form.controls.clientId.setValue(this.wiz.clientId());
      this.form.controls.authMethod.setValue(this.wiz.authMethod());
      this.form.controls.callbackUrl.setValue(this.wiz.redirectUri());
      this.form.controls.launchUrl.setValue(this.wiz.launchUrlWiz());
    }

    if (this.wiz.discovered()) {
      const env = EPIC_ENV[this.wiz.env()];
      const dv: FullDiscoveredValues = {
        fhirBaseUrl: this.wiz.baseUrl() || env.base,
        fhirVersion: 'R4 (4.0.1)',
        tokenEndpoint: this.wiz.token() || env.token,
        authzEndpoint: this.wiz.authorize() || env.authorize,
        issuer: '', jwksUri: '', introspectEp: '', revokeEp: '',
        signingAlgs: 'RS384, ES384', pkceSupport: 'S256',
        clientAuthMethods: '—',
        smartCapabilities: '', supportedScopes: '',
      };
      this.discValues.set(dv);
      this.discStatus.set('done');
      this.form.controls.epicBaseUrl.setValue(dv.fhirBaseUrl);
      this.form.controls.tokenEndpoint.setValue(dv.tokenEndpoint);
      this.form.controls.authzEndpoint.setValue(dv.authzEndpoint);
    }
    if (this.wiz.resources().length) {
      this.form.controls.resources.setValue(this.wiz.resources());
      // Editing: the saved resource types define the source's identity, so render them prepopulated (and disabled in
      // the template) instead of the pre-discovery empty-state. Seed the discovered list + mark discovery done so the
      // picker shows exactly the saved set.
      if (this.wiz.isEditing()) {
        this.discoveredResourceTypes.set(this.wiz.resources());
        if (this.discStatus() !== 'done') {
          this.discStatus.set('done');
        }
      }
    }
    if (this.wiz.stepName()) {
      this.form.controls.appName.setValue(this.wiz.stepName());
    }

    this.prevAudience = this.audience();
    this.syncValidators();

    this.form.controls.audience.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((next) => {
        const nextAudience = next as EpicAudience;
        this.clearInapplicableFields(this.prevAudience, nextAudience);
        this.prevAudience = nextAudience;
        this.syncValidators();
      });

    this.form.controls.authMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncValidators());

    this.form.controls.retrievalMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRetrievalValidators());

    this.form.controls.exportScope.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRetrievalValidators());
  }

  // ── audience field lifecycle ────────────────────────────────────────────────
  private prevAudience: EpicAudience = 'provider-ehr-launch';

  /** Clears values for fields that are no longer applicable after an audience switch. */
  private clearInapplicableFields(prev: EpicAudience, next: EpicAudience): void {
    const prevCfg = AUDIENCE_FIELD_CONFIG[prev];
    const nextCfg = AUDIENCE_FIELD_CONFIG[next];

    if (prevCfg.showRedirect && !nextCfg.showRedirect) {
      this.form.patchValue({ callbackUrl: '' });
    }

    if (prevCfg.showLaunchUrl && !nextCfg.showLaunchUrl) {
      this.form.patchValue({ launchUrl: '' });
    }

    if (prevCfg.showCdsHooks && !nextCfg.showCdsHooks) {
      this.form.patchValue({
        cdsDiscoveryUrl: '', cdsServiceEndpoint: '', cdsTriggerHook: '',
        cdsReturnCard: '', cdsDtrQuestionnaire: '',
      });
    }

    if (prevCfg.showResourcePicker && !nextCfg.showResourcePicker) {
      this.form.patchValue({ resources: [] });
    }

    if (prevCfg.showRetrieval && !nextCfg.showRetrieval) {
      this.form.patchValue({
        retrievalMethod: '',
        subscriptionResourceType: [], webhookResourceType: [], searchRestResourceType: [], bulkExportResourceType: [],
        eventType: '', notificationPayload: '',
        endpointType: '', reconciliationSchedule: '', payloadFormat: '', searchCriteria: '',
        incrementalCursor: false, schedulePollFrequency: '', runMode: '', exportScope: '',
        groupId: '', patientIdList: '', fhirOutputFormat: 'ndjson',
      });
    }
  }

  /** Applies Validators.required (and URL format checks) only to fields the current audience shows. */
  private syncValidators(): void {
    const cfg    = this.audienceConfig();
    const method = this.authMethod();

    const apply = (name: string, required: boolean, isUrl = false): void => {
      const ctrl = this.form.get(name)!;
      const validators: ValidatorFn[] = isUrl ? [urlValidator] : [];
      if (required) validators.unshift(Validators.required);
      ctrl.setValidators(validators);
      ctrl.updateValueAndValidity({ emitEvent: false });
    };

    apply('epicBaseUrl',   true, true);
    apply('clientId',      true);
    apply('tokenEndpoint', true, true);
    apply('authzEndpoint', true, true);
    apply('callbackUrl',   cfg.showRedirect, true);
    apply('launchUrl',     cfg.showLaunchUrl, true);
    apply('resources',     cfg.showResourcePicker);
    apply('clientSecret',  method === 'secret');
    apply('jwksUrl',       method === 'jwt', true);

    apply('cdsDiscoveryUrl',     cfg.showCdsHooks);
    apply('cdsServiceEndpoint',  cfg.showCdsHooks);
    apply('cdsTriggerHook',      cfg.showCdsHooks);
    apply('cdsReturnCard',       cfg.showCdsHooks);
    apply('cdsDtrQuestionnaire', cfg.showCdsHooks);

    this.syncRetrievalValidators();
  }

  /** Applies Validators.required only to the retrieval fields the selected method actually shows. */
  private syncRetrievalValidators(): void {
    const cfg       = this.audienceConfig();
    const methodCfg = cfg.showRetrieval ? this.retrievalConfig() : null;
    const visible   = methodCfg ? this.visibleRetrievalFields() : [];
    const visibleKeys = new Set(visible.map(f => f.key));

    const apply = (name: string, required: boolean): void => {
      const ctrl = this.form.get(name)!;
      ctrl.setValidators(required ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    };

    apply('retrievalMethod', cfg.showRetrieval);

    for (const key of RETRIEVAL_FIELD_KEYS) {
      const field = methodCfg?.fields.find(f => f.key === key);
      apply(key, !!field && visibleKeys.has(key) && field.required);
    }
  }

  private toggleArrayControl(name: string, value: string): void {
    const ctrl = this.form.get(name)!;
    const cur: string[] = ctrl.value ?? [];
    ctrl.setValue(cur.includes(value) ? cur.filter(x => x !== value) : [...cur, value]);
  }

  private toggleAllArrayControl(name: string, all: readonly string[]): void {
    const ctrl = this.form.get(name)!;
    const cur: string[] = ctrl.value ?? [];
    ctrl.setValue(cur.length === all.length ? [] : [...all]);
  }

  protected toggleResource(r: string): void { this.toggleArrayControl('resources', r); }
  protected toggleAllResources(): void { this.toggleAllArrayControl('resources', this.resources); }

  /** Each retrieval method owns its own Resource Type control — `key` picks which one. */
  protected toggleRetrievalResource(key: RetrievalFieldKey, r: string): void { this.toggleArrayControl(key, r); }
  protected toggleAllRetrievalResources(key: RetrievalFieldKey, all: readonly string[]): void { this.toggleAllArrayControl(key, all); }

  /** Generic invalid-and-touched check used by the config-driven retrieval field renderer. */
  protected isRetrievalFieldInvalid(field: RetrievalFieldDef): boolean {
    const ctrl = this.form.get(field.key);
    return !!ctrl && ctrl.touched && ctrl.invalid;
  }

  protected runDiscover(): void {
    const url = this.form.controls.epicBaseUrl.value.trim();
    if (!url) { this.toast.show('URL required', 'Enter Epic FHIR Base URL first.'); return; }
    const envKey: EnvKey = this.form.controls.environment.value === 'production' ? 'production' : 'sandbox';
    this.discStatus.set('loading');

    this.discovery.discover(url, envKey).subscribe({
      next: (result) => {
        const dv: FullDiscoveredValues = {
          fhirBaseUrl: url, fhirVersion: 'R4 (4.0.1)',
          tokenEndpoint: result.token, authzEndpoint: result.authorize,
          issuer: 'https://fhir.epic.com/interconnect-fhir-oauth',
          jwksUri: 'https://fhir.epic.com/interconnect-fhir-oauth/.well-known/jwks.json',
          introspectEp: result.token.replace('/token', '/introspect'),
          revokeEp: result.token.replace('/token', '/revoke'),
          signingAlgs: 'RS384, ES384',
          pkceSupport: result.codeChallengeMethods.length ? result.codeChallengeMethods.join(', ') : 'S256',
          clientAuthMethods: result.tokenEndpointAuthMethods.length ? result.tokenEndpointAuthMethods.join(', ') : '—',
          smartCapabilities: result.capabilities.length ? result.capabilities.join(', ') : '—',
          supportedScopes: result.scopesSupported.length ? result.scopesSupported.join(' ') : '—',
        };
        this.discValues.set(dv);
        this.discoveredScopes.set(result.scopesSupported);
        // Resource Type: Auto — from the source's /metadata.
        this.discoveredResourceTypes.set(result.resourceTypes);
        // SMART Scope Version: Auto — prefer Epic's advertised permission-v1/permission-v2 capabilities; if neither is
        // present (Epic often omits them), infer from the shape of scopes_supported — granular v2 suffixes (.rs/.cruds/…)
        // vs coarse v1 (.read/.write). Only badge it as auto-detected when we actually determined a version.
        const detected = detectScopeVersion(result.capabilities, result.scopesSupported);
        if (detected) {
          this.form.controls.scopeVersion.setValue(detected);
          this.scopeVersionAuto.set(true);
        } else {
          this.scopeVersionAuto.set(false);
        }
        // Client Auth Method: Auto only for Backend System, where SMART Backend Services mandates JWT
        // (private_key_jwt) — interactive audiences keep whatever the user picked (see detectAuthMethod for why).
        const detectedAuthMethod = detectAuthMethod(this.audience(), result.tokenEndpointAuthMethods);
        if (detectedAuthMethod) {
          this.form.controls.authMethod.setValue(detectedAuthMethod);
          this.authMethodAuto.set(true);
        } else {
          this.authMethodAuto.set(false);
        }
        this.form.controls.tokenEndpoint.setValue(dv.tokenEndpoint);
        this.form.controls.authzEndpoint.setValue(dv.authzEndpoint);
        this.wiz.token.set(dv.tokenEndpoint);
        this.wiz.authorize.set(dv.authzEndpoint);
        this.wiz.baseUrl.set(url);
        this.wiz.setDiscovered(true);
        this.discStatus.set('done');
        if (result.resourceTypesError) {
          this.toast.show('Discovery complete (partial)', `Endpoints resolved. Resource types unavailable: ${result.resourceTypesError}`);
        } else {
          this.toast.show('Discovery complete', `Resolved endpoints + ${result.resourceTypes.length} resource types.`);
        }
      },
      error: (err) => {
        this.discStatus.set('error');
        const msg = err?.error?.error ?? err?.error ?? err?.message ?? 'Check the URL or enter endpoints manually.';
        this.toast.show('Discovery failed', typeof msg === 'string' ? msg : 'Check the URL or enter endpoints manually.');
      },
    });
  }

  protected runTestConnection(): void {
    const baseUrl = this.form.controls.epicBaseUrl.value.trim();
    if (!baseUrl) { this.toast.show('Base URL required', 'Enter the Epic FHIR base URL first.'); return; }
    this.testStatus.set('running');
    // Real reachability test: probe the source's public SMART/metadata endpoints via the backend.
    this.discovery.discover(baseUrl).subscribe({
      next: () => {
        this.testStatus.set('ok');
        this.toast.show('Test passed', 'Reached the source SMART configuration endpoint.');
      },
      error: (err) => {
        this.testStatus.set('fail');
        const msg = err?.error?.error ?? err?.error ?? err?.message ?? 'Could not reach the source endpoint.';
        this.toast.show('Test failed', typeof msg === 'string' ? msg : 'Could not reach the source endpoint.');
      },
    });
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      // Wait one tick so the error classes/messages just triggered by markAllAsTouched
      // are in the DOM before we measure scroll position and focus the field.
      setTimeout(() => this.focusFirstInvalidField());
      return;
    }
    const v = this.form.value;

    const aud      = v.audience as EpicAudience;
    const envKey: EnvKey = v.environment === 'production' ? 'production' : 'sandbox';
    const appKeyMap: Record<EpicAudience, AppKey> = {
      'provider-ehr-launch': 'provider-ehr-launch',
      'provider-standalone': 'provider-standalone',
      'backend-system':      'backend-system',
      'patient':             'patient-standalone',
    };

    const cfg = this.audienceConfig();

    this.wiz.setAppKey(appKeyMap[aud]);
    this.wiz.setEnv(envKey);
    this.wiz.stepName.set(v.appName ?? 'Epic');
    this.wiz.baseUrl.set(v.epicBaseUrl ?? '');
    this.wiz.token.set(v.tokenEndpoint ?? '');
    this.wiz.authorize.set(v.authzEndpoint ?? '');
    // EHR launch requires ≥1 trusted issuer server-side; default it to the FHIR base URL (the iss Epic sends) so
    // create-on-save (build) passes validation. Only meaningful for the EHR-launch audience.
    if (aud === 'provider-ehr-launch') {
      this.wiz.trustedIssuers.set((v.epicBaseUrl ?? '').trim());
    }
    // Backend System has no shared Resource Type picker — fall back to whichever
    // retrieval method's own Resource Type list is currently set.
    this.wiz.resources.set(cfg.showResourcePicker ? (v.resources ?? []) : this.activeRetrievalResourceTypes());

    this.wiz.save({
      stepName:    v.appName ?? 'Epic',
      baseUrl:     v.epicBaseUrl ?? '',
      token:       v.tokenEndpoint ?? '',
      authorize:   v.authzEndpoint ?? '',
      algorithm:   'RS384',
      jwksMethod:  v.authMethod === 'jwt' ? 'hosted' : 'external',
      jwksUrl:     v.jwksUrl ?? '',
      kid:         '',
      kvRef:       v.privateKeyRef ?? '',
      redirectUri: v.callbackUrl ?? '',
      launchUrl:   v.launchUrl ?? '',
    }, {
      'Client ID':             v.clientId ?? '',
      'Auth method':           v.authMethod ?? 'secret',
      'Epic audience':         aud,
      'SMART version':         'SMART App Launch 2.0 (R4)',
      'Scope version':         v.scopeVersion === 'v1' ? 'v1 (coarse)' : 'v2 (granular)',
      // The actual, discovery-validated scope string this form built and showed the user — takes priority over
      // WizardService.save()'s own ScopeBuilderService-derived default (which knows nothing about the selected
      // resources, scope version, or audience-specific scopes this form computed).
      'Scopes':                this.scopeString().split(/\s+/).filter(Boolean).join(' '),
      'CDS discovery URL':     v.cdsDiscoveryUrl ?? '',
      'CDS service endpoint':  v.cdsServiceEndpoint ?? '',
      'CDS trigger hook':      v.cdsTriggerHook ?? '',
      'CDS return card':       v.cdsReturnCard ?? '',
      'DTR questionnaire':     v.cdsDtrQuestionnaire ?? '',
      ...(cfg.showRetrieval ? {
        'Data retrieval method':     this.retrievalConfig()?.label ?? '',
        'Retrieval resource type':   this.activeRetrievalResourceTypes().join(', '),
        'Event type':                v.eventType ?? '',
        'Notification payload':      v.notificationPayload ?? '',
        'Endpoint type':             v.endpointType ?? '',
        'Reconciliation schedule':   v.reconciliationSchedule ?? '',
        'Payload format':            v.payloadFormat ?? '',
        'Search criteria':           v.searchCriteria ?? '',
        'Incremental cursor':        v.incrementalCursor ? 'enabled' : 'disabled',
        'Schedule / poll frequency': v.schedulePollFrequency ?? '',
        'Run mode':                  v.runMode ?? '',
        'Export scope':              v.exportScope ?? '',
        'Group ID':                  v.groupId ?? '',
        'Patient ID / list':         v.patientIdList ?? '',
        'FHIR output format':        v.fhirOutputFormat ?? '',
      } : {}),
    });

    this.saved.emit();
  }

  protected cancel(): void { this.cancelled.emit(); }

  protected async copyRedirectUri(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.form.controls.callbackUrl.value);
      this.toast.show('Copied', 'Redirect URI copied to clipboard.');
    } catch {
      this.toast.show('Copy failed', 'Select the text manually.');
    }
  }

  /**
   * Focuses and smoothly scrolls to the first invalid field, in the order fields
   * actually appear in the (audience-dependent) rendered form — not FormGroup
   * declaration order. Works for any control bound via formControlName, plus the
   * checkbox-driven `resources` control via its [data-control] wrapper.
   */
  private focusFirstInvalidField(): void {
    const root = this.formRoot?.nativeElement;
    if (!root) return;

    const candidates = root.querySelectorAll<HTMLElement>('[formcontrolname], [data-control]');
    for (const el of Array.from(candidates)) {
      const name = el.getAttribute('formcontrolname') ?? el.getAttribute('data-control');
      const ctrl = name ? this.form.get(name) : null;
      if (!ctrl || ctrl.valid) continue;

      const focusTarget = el.matches('input, select, textarea')
        ? el
        : (el.querySelector<HTMLElement>('input, select, textarea') ?? el);

      el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      focusTarget.focus({ preventScroll: true });
      return;
    }
  }
}
