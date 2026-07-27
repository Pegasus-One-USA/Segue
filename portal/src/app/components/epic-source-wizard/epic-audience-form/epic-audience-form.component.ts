import {
  Component, output, inject, signal, computed, effect, OnInit, DestroyRef, ElementRef, ViewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule, FormBuilder, Validators, ValidatorFn, AbstractControl, ValidationErrors } from '@angular/forms';
import { WizardService, APPLICATION_TYPE_TO_AUDIENCE, AUTHENTICATION_TYPE_TO_AUTH_METHOD } from '../../../services/wizard.service';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { ToastService } from '../../../services/toast.service';
import { EPIC_ENV } from '../../../data/epic-environments.data';
import { EnvKey } from '../../../models/epic-env.model';
import { AppKey } from '../../../models/epic-app.model';
import { FullDiscoveredValues } from '../models/epic-config.model';
import { EpicAudience, AudienceFieldConfig, AUDIENCE_FIELD_CONFIG } from '../models/audience-field-config.data';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { environment } from '../../../../environments/environment';
import { OAUTH_DEFAULT_URLS } from '../../../core/api-endpoints';

export type { EpicAudience };

/** EHR/vendor selector options — values must be exact SourceSystemType enum member names (see
 *  src/FHIRBridge.Domain/Enums/SourceSystemType.cs), since the backend deserializes this field as a string enum.
 *  NewEHR / NewEHRTwo are internal placeholder enum members with no real vendor identity and are omitted. */
export const EHR_OPTIONS: { value: EhrVendor; label: string }[] = [
  { value: 'Epic',               label: 'Epic' },
  { value: 'Cerner',             label: 'Oracle Health (Cerner)' },
  { value: 'Athenahealth',       label: 'Athenahealth' },
  { value: 'MeditechGreenfield', label: 'Meditech' },
  { value: 'Healow',             label: 'eClinicalWorks (Healow)' },
  { value: 'Allscripts',         label: 'Allscripts' },
  { value: 'GenericFhir',        label: 'Generic FHIR' },
  { value: 'Hl7v2',              label: 'HL7 v2' },
  { value: 'Sample',             label: 'Sample' },
];

// MVP1 resource set — keep in sync with portal/src/app/data/scope-constants.data.ts's FHIR_RESOURCES.
const FHIR_RESOURCES = [
  'Patient', 'Practitioner', 'Encounter', 'AllergyIntolerance', 'Observation',
  'Condition', 'Procedure', 'ServiceRequest', 'DiagnosticReport',
  'MedicationRequest', 'MedicationAdministration',
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
  const hasV1 = capabilities.includes('permission-v1');
  const hasV2 = capabilities.includes('permission-v2');
  // Only trust the capability token when exactly one is advertised. Epic's system-level (Backend System) scopes
  // commonly advertise BOTH permission-v1 and permission-v2 at once — that says the server can accept either,
  // not which one this specific app was actually registered under (same ambiguity detectAuthMethod already
  // accounts for with token_endpoint_auth_methods_supported). Blindly preferring v2 there previously forced
  // every Backend System connection onto granular '.rs' scopes even when the app was registered for coarse
  // '.read' v1 scopes. Fall through to shape-inference from scopes_supported instead of guessing.
  if (hasV1 && !hasV2) return 'v1';
  if (hasV2 && !hasV1) return 'v2';
  const suffix = (s: string): string => (s.includes('.') ? s.slice(s.lastIndexOf('.') + 1) : '').toLowerCase();
  if (scopesSupported.some(s => /^[cruds]+$/.test(suffix(s)))) return 'v2';
  if (scopesSupported.some(s => suffix(s) === 'read' || suffix(s) === 'write')) return 'v1';
  return null;
}

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
  | 'exportScope' | 'groupId' | 'patientIdList' | 'fhirOutputFormat'
  | 'pageSize' | 'sortOrder' | 'includeLinked' | 'revIncludeLinked' | 'retryPolicy' | 'timeoutSeconds' | 'maxRecordsPerRun'
  | 'fullRefreshRecurrence' | 'fullRefreshDaysOfWeek' | 'fullRefreshDayOfMonth' | 'fullRefreshTime';

const RETRIEVAL_FIELD_KEYS: readonly RetrievalFieldKey[] = [
  'subscriptionResourceType', 'webhookResourceType', 'searchRestResourceType', 'bulkExportResourceType',
  'eventType', 'notificationPayload', 'endpointType', 'reconciliationSchedule',
  'payloadFormat', 'searchCriteria', 'incrementalCursor', 'schedulePollFrequency', 'runMode',
  'exportScope', 'groupId', 'patientIdList', 'fhirOutputFormat',
  'pageSize', 'sortOrder', 'includeLinked', 'revIncludeLinked', 'retryPolicy', 'timeoutSeconds', 'maxRecordsPerRun',
  'fullRefreshRecurrence', 'fullRefreshDaysOfWeek', 'fullRefreshDayOfMonth', 'fullRefreshTime',
];

interface RetrievalFieldOption { value: string; label: string; }

/** Snapshot of the values other fields' visibility can depend on — passed to {@link RetrievalFieldDef.visibleWhen}. */
interface RetrievalFieldVisibilityContext {
  exportScope: string;
  runMode: string;
  fullRefreshRecurrence: string;
  /** Drives the Standalone (one-shot, user-initiated) vs. Backend System (automated, scheduled) field split. */
  retrievalScope: AudienceFieldConfig['retrievalScope'];
}

interface RetrievalFieldDef {
  key: RetrievalFieldKey;
  label: string;
  type: 'select' | 'text' | 'textarea' | 'checkbox' | 'multiselect' | 'weekday-picker' | 'time';
  required: boolean;
  options?: readonly RetrievalFieldOption[];
  placeholder?: string;
  hint?: string;
  /** Governs whether this field renders at all, given the current export scope / run mode / recurrence choice. */
  visibleWhen?: (ctx: RetrievalFieldVisibilityContext) => boolean;
  /** Field is only required while another field does NOT hold the given value (e.g. schedule isn't required when Run Mode is Manual Only). */
  requiredUnless?: { key: RetrievalFieldKey; value: string };
  /**
   * Rendered inside the collapsed "Advanced Search Options" disclosure instead of the main field grid. A function
   * lets a field be "advanced" for one retrieval scope and promoted to the main grid for another (e.g. Max Results
   * and Include Related Resources are two of only four fields Standalone sees, so they belong in the main grid
   * there, while they stay tucked away for Backend System's much larger field set).
   */
  advanced?: boolean | ((ctx: RetrievalFieldVisibilityContext) => boolean);
}

function isFieldAdvanced(field: RetrievalFieldDef, ctx: RetrievalFieldVisibilityContext): boolean {
  return typeof field.advanced === 'function' ? field.advanced(ctx) : !!field.advanced;
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

const SORT_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '_lastUpdated',  label: '_lastUpdated (oldest → newest)' },
  { value: '-_lastUpdated', label: '_lastUpdated (newest → oldest)' },
  { value: 'date',          label: 'date (ascending)' },
  { value: '-date',         label: 'date (descending)' },
];

const RETRY_POLICY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'none',        label: 'No retry' },
  { value: 'fixed-3',     label: 'Fixed — 3 attempts' },
  { value: 'exponential', label: 'Exponential backoff' },
];

// ── Full Refresh calendar recurrence (Google Calendar-style: anchored to a specific time, not an interval) ──────
const FULL_REFRESH_RECURRENCE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'daily',   label: 'Daily' },
  { value: 'weekly',  label: 'Weekly' },
  { value: 'monthly', label: 'Monthly' },
];

// cron day-of-week: 0 = Sunday … 6 = Saturday.
export const WEEKDAY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '0', label: 'Sun' }, { value: '1', label: 'Mon' }, { value: '2', label: 'Tue' },
  { value: '3', label: 'Wed' }, { value: '4', label: 'Thu' }, { value: '5', label: 'Fri' }, { value: '6', label: 'Sat' },
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
    description: 'Epic (or a middleware relay) posts updates to a Segue callback endpoint.',
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
    description: 'Segue polls Epic’s FHIR REST API on a schedule.',
    fields: [
      // Resource Type / Search Criteria / Max Results / Include Related Resources are the only four fields shown to
      // Provider Standalone (one-shot, user-initiated) — everything else here is scheduling/automation plumbing
      // that only makes sense for Backend System's unattended, recurring execution.
      //
      // For Standalone this field is hidden — it reuses the shared Resource Type & Scopes picker (Section 5)
      // instead of a second, separate multiselect, since that picker already drives the SMART scopes this
      // connection's one-shot fetch runs under.
      { key: 'searchRestResourceType', label: 'Resource Type',                   type: 'multiselect', required: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' },
      { key: 'searchCriteria',        label: 'Search Criteria',                  type: 'text',         required: false, placeholder: 'status=active&category=vital-signs', hint: 'Optional FHIR search parameters appended to every request.' },
      { key: 'runMode',               label: 'Run Mode',                         type: 'select',       required: true, visibleWhen: ctx => ctx.retrievalScope === 'automated', options: [
        { value: 'incremental', label: 'Incremental Sync' },
        { value: 'full',        label: 'Full Refresh' },
        { value: 'manual',      label: 'Manual Only' },
      ], hint: 'Incremental Sync polls only changed records on a short interval; Full Refresh reloads everything on a calendar schedule; Manual Only runs on demand.' },
      { key: 'incrementalCursor',     label: 'Incremental Sync (_lastUpdated)',  type: 'checkbox',    required: false, visibleWhen: ctx => ctx.retrievalScope === 'automated', hint: 'Only fetch resources changed since the last successful run. Enabled automatically when Run Mode is Incremental Sync.' },
      // Incremental Sync wants a tight polling interval; Full Refresh wants a calendar-anchored time (off-hours,
      // low-traffic) — these are different backend trigger primitives (Poll+minutes vs Schedule+cron), so they get
      // different controls rather than forcing one picker to do both jobs. None of this applies to Standalone —
      // a user-initiated one-shot fetch has no recurring schedule to configure.
      { key: 'schedulePollFrequency', label: 'Schedule / Poll Frequency',        type: 'select',       required: true, options: POLL_FREQUENCY_OPTIONS, requiredUnless: { key: 'runMode', value: 'manual' }, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode !== 'full', hint: 'Not required when Run Mode is Manual Only — the pipeline only runs when triggered.' },
      { key: 'fullRefreshRecurrence',  label: 'Repeat',                           type: 'select',       required: true, options: FULL_REFRESH_RECURRENCE_OPTIONS, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full', hint: 'Full Refresh reloads everything with no incremental filter — anchor it to a specific, low-traffic time rather than a tight interval.' },
      { key: 'fullRefreshDaysOfWeek', label: 'On',                               type: 'weekday-picker', required: true, options: WEEKDAY_OPTIONS, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full' && ctx.fullRefreshRecurrence === 'weekly' },
      { key: 'fullRefreshDayOfMonth', label: 'Day of month',                     type: 'select',       required: true, options: Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: `${i + 1}` })), visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full' && ctx.fullRefreshRecurrence === 'monthly', hint: 'Capped at 28 so it fires every month, including February.' },
      { key: 'fullRefreshTime',       label: 'At',                               type: 'time',          required: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full', hint: 'Server local time. Pick an off-hours slot to avoid contending with interactive EHR traffic.' },
      // ── Max Results / Include Related Resources: main-grid fields for Standalone, tucked into "Advanced Search
      // Options" for Backend System (unchanged Backend behavior — just joined by two new promoted-for-Standalone
      // fields below). ─────────────────────────────────────────────────────────
      { key: 'maxRecordsPerRun',  label: 'Max Results',                     type: 'text',   required: false, placeholder: 'e.g. 50000', hint: 'Safety cap — stops fetching once this many records are returned.', advanced: ctx => ctx.retrievalScope === 'automated' },
      { key: 'includeLinked',     label: 'Include Related Resources (_include)', type: 'text', required: false, placeholder: 'Encounter:patient', hint: 'Comma-separated _include parameters to pull referenced resources in the same response.', advanced: ctx => ctx.retrievalScope === 'automated' },
      // ── Advanced Search Options (Backend System only — collapsed by default) ────
      { key: 'pageSize',           label: 'Page Size (_count)',              type: 'text',   required: false, placeholder: '100', hint: 'Resources requested per page. The server may cap this lower than requested.', advanced: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' },
      { key: 'sortOrder',          label: 'Sort (_sort)',                    type: 'select', required: false, options: SORT_OPTIONS, hint: 'Sort order applied to each search request.', advanced: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' },
      { key: 'revIncludeLinked', label: 'Reverse Include (_revinclude)',    type: 'text',   required: false, placeholder: 'Observation:patient', hint: 'Comma-separated _revinclude parameters to pull resources that reference the selected type.', advanced: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' },
      { key: 'retryPolicy',       label: 'Retry Policy',                     type: 'select', required: false, options: RETRY_POLICY_OPTIONS, hint: 'How failed requests are retried before the run is marked failed.', advanced: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' },
      { key: 'timeoutSeconds',    label: 'Timeout (seconds)',                type: 'text',   required: false, placeholder: '30', hint: 'Per-request timeout before the connector aborts and retries.', advanced: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' },
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
      { key: 'groupId',               label: 'Group ID',                     type: 'text',         required: true, placeholder: 'e.g. 4diBHMQR-nurOSMS8UbGqQB', hint: 'Epic Group FHIR ID to export.', visibleWhen: ctx => ctx.exportScope === 'group' },
      { key: 'patientIdList',         label: 'Patient ID / Patient List',    type: 'textarea',     required: true, placeholder: 'Comma-separated Patient FHIR IDs', hint: 'One or more Patient FHIR IDs to export.', visibleWhen: ctx => ctx.exportScope === 'patient' },
      { key: 'incrementalCursor',     label: 'Incremental Cursor (_since)',  type: 'checkbox',     required: false, hint: 'Only export resources changed since the last successful export.' },
      { key: 'fhirOutputFormat',      label: 'FHIR Output Format',           type: 'select',       required: true, options: [
        { value: 'ndjson',      label: 'NDJSON (application/fhir+ndjson)' },
        { value: 'ndjson-gzip', label: 'NDJSON (gzip compressed)' },
      ] },
      // Bulk $export is a heavy operation and servers (e.g. Epic) cap its frequency (~once/24h), so it schedules on a
      // calendar "Repeat" (min daily) — the same recurrence control as Search-REST Full Refresh — never a tight poll
      // frequency. System and Group exports repeat on a schedule; a Patient ID list is a one-off, so it stays manual
      // (no recurrence fields shown).
      { key: 'fullRefreshRecurrence', label: 'Repeat',        type: 'select',         required: true, options: FULL_REFRESH_RECURRENCE_OPTIONS, visibleWhen: ctx => ctx.exportScope !== '' && ctx.exportScope !== 'patient', hint: 'How often to re-run this export. Patient ID list exports run manually and are not scheduled.' },
      { key: 'fullRefreshDaysOfWeek', label: 'On',            type: 'weekday-picker', required: true, options: WEEKDAY_OPTIONS, visibleWhen: ctx => ctx.exportScope !== 'patient' && ctx.fullRefreshRecurrence === 'weekly' },
      { key: 'fullRefreshDayOfMonth', label: 'Day of month',  type: 'select',         required: true, options: Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: `${i + 1}` })), visibleWhen: ctx => ctx.exportScope !== 'patient' && ctx.fullRefreshRecurrence === 'monthly', hint: 'Capped at 28 so it fires every month, including February.' },
      { key: 'fullRefreshTime',       label: 'At',            type: 'time',           required: true, visibleWhen: ctx => ctx.exportScope !== '' && ctx.exportScope !== 'patient', hint: 'Server local time. Pick an off-hours slot to avoid contending with interactive EHR traffic.' },
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

  // The "Application URLs" section (Launch URL / Redirect URI) is hidden — both fields are now always
  // resolved from the actual deployment host (see OAUTH_DEFAULT_URLS) rather than admin-entered, so there's
  // nothing left here for an admin to read or edit. The form controls (and their validators/defaults) stay
  // exactly as they were; only the visible section is suppressed.
  protected readonly showApplicationUrlsSection = false;

  @ViewChild('formRoot') private readonly formRoot?: ElementRef<HTMLElement>;

  protected readonly wiz       = inject(WizardService);
  private  readonly discovery  = inject(EpicDiscoveryService);
  private  readonly toast      = inject(ToastService);
  private  readonly fb         = inject(FormBuilder);
  private  readonly destroyRef = inject(DestroyRef);
  private  readonly sourceConnectionSvc = inject(ISourceConnectionService);

  protected readonly ehrOptions = EHR_OPTIONS;
  /** True only when opened in read-only View mode from the Source Connections page — disables every control and
   *  hides Save. Decided once at open time (see ngOnInit), never toggled live within a single open session. */
  protected get isReadonly(): boolean { return this.wiz.readonlyMode(); }

  // ── New Source / Existing Source (canvas-mode create only) ─────────────────
  protected readonly sourceMode = signal<'new' | 'existing'>('new');
  protected readonly existingConnections = signal<SourceConnectionModel[]>([]);
  protected readonly loadingExisting = signal(false);
  protected readonly selectedExistingId = signal<string | null>(null);
  /** Every saved source connection's name (all vendors, not just Epic — getAll() returns everything, this
   *  component just filters existingConnections down to Epic for the dropdown). Populated on ngOnInit for any
   *  canvas-mode create (see loadAllConnectionNames) and refreshed by onSourceModeChange when the picker switches
   *  to "Existing Source". save() dedupes against it in both branches — cloning an existing connection verbatim,
   *  and leaving a brand-new node on its default/reused App Name — otherwise the create call collides with
   *  "A source connection named '<name>' already exists." only at workflow-build time. */
  private _allConnectionNames = new Set<string>();
  /** Only offered when creating a brand-new canvas node — editing an existing node already has its own data, and
   *  entity mode (Source Connections page) has its own dedicated Create flow, no "clone from existing" need yet. */
  protected readonly showSourcePicker = computed(() => this.wiz.wizardMode() === 'canvas' && !this.isEditing);

  // Snapshot of the form's raw value taken once discovery settles after populateFormFromSourceConnection() patches
  // it in — compared against the current form value at save time (hasExistingChanged) to decide "reuse as-is" vs
  // "fork a new connection", mirroring destination-wizard.component.ts's _existingBaseline/hasExistingChanged.
  private _existingBaseline: Record<string, unknown> | null = null;
  // Discovery (runDiscover) can still auto-patch scopeVersion/authMethod/discoveredResourceTypes after the clone's
  // patchValue call returns, so the baseline is captured once discovery's async next/error handler actually runs —
  // snapshotting immediately would make those auto-detected values look like user edits on every single clone.
  private _awaitingBaselineSnapshot = false;

  // Resource Type list: always the platform's curated MVP1 set (FHIR_RESOURCES), regardless of what Discover
  // returns — the backend's /metadata probe reflects everything the endpoint's CapabilityStatement supports
  // (often 50+ types, filtered only by read-interaction support), not what this pipeline can actually process.
  // discoveredResourceTypes is still populated for the separate scope-validation cross-check below
  // (unsupportedScopes/scopesValidatedByDiscovery) — it just no longer drives which checkboxes are offered.
  protected readonly discoveredResourceTypes = signal<string[]>([]);
  protected get resources(): string[] {
    return FHIR_RESOURCES;
  }

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
    jwtKid:              [''],
    privateKeyRef:       [''],
    privateKeySecretName: [''],
    // Backend System + JWT only: 'manual' (default — preserves existing behavior for every connection created
    // before this control existed) lets the admin type kid/vault-name/secret-name themselves, exactly as before.
    // 'gen'/'import' hand key provisioning to SigningKeyGenerationService instead — see generateKeyPair()/
    // importPrivateKey() below.
    keySource:           ['manual' as 'manual' | 'gen' | 'import'],
    launchUrl:         [OAUTH_DEFAULT_URLS.launchUrl, urlValidator],
    // How the app is registered to open within the EHR (EHR-launch audience only) — mirrors Epic's own Hyperspace/
    // Hyperdrive app-launch configuration. FHIRBridge doesn't control this behavior; it's recorded for admins.
    launchDisplayMode: ['Embedded'],
    callbackUrl:       [OAUTH_DEFAULT_URLS.redirectUri, [Validators.required, urlValidator]],
    resources:         [[] as string[], Validators.required],
    scopeVersion:      ['v2'],
    appName:           ['Segue Epic'],
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
    // ── Full Refresh calendar recurrence (Search REST, Run Mode = Full Refresh only) ────────────────────────────
    fullRefreshRecurrence:  ['daily'],
    fullRefreshDaysOfWeek:  [[] as string[]],
    fullRefreshDayOfMonth:  ['1'],
    fullRefreshTime:        ['02:00'],
    // ── Advanced Search Options (Search REST only) ──────────────────────────────
    pageSize:               ['100'],
    sortOrder:              [''],
    includeLinked:          [''],
    revIncludeLinked:       [''],
    retryPolicy:            ['exponential'],
    timeoutSeconds:         ['30'],
    maxRecordsPerRun:       [''],
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
  private readonly runModeValue         = toSignal(this.form.controls.runMode.valueChanges,          { initialValue: this.form.controls.runMode.value });
  private readonly scopeVersionValue    = toSignal(this.form.controls.scopeVersion.valueChanges,    { initialValue: this.form.controls.scopeVersion.value });
  private readonly fullRefreshRecurrenceValue = toSignal(this.form.controls.fullRefreshRecurrence.valueChanges, { initialValue: this.form.controls.fullRefreshRecurrence.value });
  private readonly fullRefreshDaysOfWeekValue = toSignal(this.form.controls.fullRefreshDaysOfWeek.valueChanges, { initialValue: this.form.controls.fullRefreshDaysOfWeek.value });
  private readonly fullRefreshDayOfMonthValue = toSignal(this.form.controls.fullRefreshDayOfMonth.valueChanges, { initialValue: this.form.controls.fullRefreshDayOfMonth.value });
  private readonly fullRefreshTimeValue       = toSignal(this.form.controls.fullRefreshTime.valueChanges,       { initialValue: this.form.controls.fullRefreshTime.value });

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

  // ── Backend Services signing key: generate / import (see generateKeyPair()/importPrivateKey() below) ──────────
  private readonly keySourceValue = toSignal(this.form.controls.keySource.valueChanges, { initialValue: this.form.controls.keySource.value });
  protected readonly keySource = computed(() => this.keySourceValue());
  protected readonly isGenKey    = computed(() => this.keySource() === 'gen');
  protected readonly isImportKey = computed(() => this.keySource() === 'import');
  protected readonly keyGenStatus = signal<'idle' | 'generating' | 'generated'>('idle');
  /** Name of the file chosen for "Import Existing Private Key" — read client-side only; nothing is sent to the
   *  backend until "Import Private Key" is clicked. */
  protected readonly selectedFileName = signal<string | null>(null);
  private pendingPrivateKeyPem: string | null = null;

  // ── existing-key guard (prevents Generate/Import from silently overwriting a key that's already saved) ────────
  private readonly jwtKidValue = toSignal(this.form.controls.jwtKid.valueChanges, { initialValue: this.form.controls.jwtKid.value });
  private readonly privateKeyRefValue = toSignal(this.form.controls.privateKeyRef.valueChanges, { initialValue: this.form.controls.privateKeyRef.value });
  private readonly privateKeySecretNameValue = toSignal(this.form.controls.privateKeySecretName.valueChanges, { initialValue: this.form.controls.privateKeySecretName.value });
  /** True whenever Key ID/Key Vault Name/Secret Name are all populated right now — regardless of how they got
   *  there (typed, restored from a saved connection, or just Generated/Imported this session). Drives the
   *  required-field validation being satisfied; NOT by itself the "show the guard panel" signal below. */
  protected readonly hasExistingSigningKey = computed(() =>
    !!(this.jwtKidValue() && this.privateKeyRefValue() && this.privateKeySecretNameValue())
  );
  /** True only when the key came from a SAVED connection (restoreExtendedFieldsFromEditingNode() /
   *  populateFormFromSourceConnection()) — false right after a fresh Generate/Import in this same session, which
   *  already has its own "✓ key ready" affordance and doesn't need the extra guard. This is what actually decides
   *  whether the read-only "already configured" panel (vs. the normal selector) renders. */
  protected readonly keyLoadedFromExistingConnection = signal(false);
  /** True once the admin has explicitly clicked "Replace Key" past the guard panel — reveals the normal
   *  Manual/Generate/Import selector so they can proceed. */
  protected readonly replacingKey = signal(false);

  protected startReplacingKey(): void {
    this.replacingKey.set(true);
  }

  protected cancelReplacingKey(): void {
    this.replacingKey.set(false);
    this.keyGenStatus.set('idle');
    this.selectedFileName.set(null);
    this.pendingPrivateKeyPem = null;
  }

  // ── real JWKS URL (computed live from the connection id, once known — never typed/stored as free text) ────────
  /** The real SourceConnection id for whatever this form is currently editing, from whichever restore path
   *  applies: entity mode (WizardService.entityId) or canvas mode editing an already-built workflow's node
   *  (WizardService.editingFields()['sourceConnectionId'], embedded there by WorkflowEndpoints on the original
   *  build). Null for a brand-new node/connection that hasn't been saved yet. */
  protected readonly resolvedSourceConnectionId = computed(() =>
    this.wiz.entityId() || this.wiz.editingFields()?.['sourceConnectionId'] || null
  );
  /** Only meaningful for a Generated/Imported key — an externally-hosted (manual) JWKS URL is whatever the admin
   *  typed, not something FHIRBridge can compute. Null until resolvedSourceConnectionId() is known (i.e., before
   *  the very first save) — see WorkflowBuilderComponent.reconcileGeneratedJwksUrls() for how the placeholder
   *  gets corrected once it is. */
  protected readonly liveJwksUrl = computed(() => {
    const id = this.resolvedSourceConnectionId();
    return id ? `${environment.apiBase}/api/v1/source-connections/${id}/.well-known/jwks.json` : null;
  });

  // ── Data Retrieval Method (Backend System: all four methods; Standalone: Search REST only, one-shot) ──────────
  /** Standalone only ever offers Search REST — Subscription/Webhook/Bulk Export are async, unattended patterns
   *  that don't fit a user-initiated, one-shot launch. */
  protected readonly retrievalMethodOptions = computed(() => {
    const all = Object.values(RETRIEVAL_METHOD_CONFIG).map(c => ({ value: c.value, label: c.label }));
    return this.audienceConfig().retrievalScope === 'oneshot'
      ? all.filter(o => o.value === 'search-rest')
      : all;
  });

  protected readonly retrievalMethod = computed(() => this.retrievalMethodValue() as RetrievalMethod | '');
  protected readonly retrievalConfig = computed(() => {
    const method = this.retrievalMethod();
    return method ? RETRIEVAL_METHOD_CONFIG[method] : null;
  });

  private readonly retrievalVisibilityContext = computed<RetrievalFieldVisibilityContext>(() => ({
    exportScope: this.exportScopeValue(),
    runMode: this.runModeValue(),
    fullRefreshRecurrence: this.fullRefreshRecurrenceValue(),
    retrievalScope: this.audienceConfig().retrievalScope,
  }));

  protected readonly visibleRetrievalFields = computed(() => {
    const cfg = this.retrievalConfig();
    if (!cfg) return [];
    const ctx = this.retrievalVisibilityContext();
    return cfg.fields.filter(f => !isFieldAdvanced(f, ctx) && (!f.visibleWhen || f.visibleWhen(ctx)));
  });

  /** Fields rendered inside the collapsed "Advanced Search Options" disclosure. */
  protected readonly advancedRetrievalFields = computed(() => {
    const cfg = this.retrievalConfig();
    if (!cfg) return [];
    const ctx = this.retrievalVisibilityContext();
    return cfg.fields.filter(f => isFieldAdvanced(f, ctx) && (!f.visibleWhen || f.visibleWhen(ctx)));
  });

  /** Google-Calendar-style summary + the cron expression it compiles to, for Run Mode = Full Refresh. */
  protected readonly fullRefreshCronExpression = computed(() => {
    const [hh, mm] = (this.fullRefreshTimeValue() || '02:00').split(':');
    const minute = Number(mm) || 0;
    const hour = Number(hh) || 0;
    switch (this.fullRefreshRecurrenceValue()) {
      case 'weekly': {
        const days = this.fullRefreshDaysOfWeekValue();
        return days?.length ? `${minute} ${hour} * * ${days.join(',')}` : null;
      }
      case 'monthly':
        return `${minute} ${hour} ${this.fullRefreshDayOfMonthValue() || '1'} * *`;
      default:
        return `${minute} ${hour} * * *`;
    }
  });

  protected readonly fullRefreshCronSummary = computed(() => {
    const cron = this.fullRefreshCronExpression();
    const time = this.fullRefreshTimeValue() || '02:00';
    switch (this.fullRefreshRecurrenceValue()) {
      case 'weekly': {
        const days = this.fullRefreshDaysOfWeekValue() ?? [];
        const labels = WEEKDAY_OPTIONS.filter(o => days.includes(o.value)).map(o => o.label);
        return labels.length ? `Weekly on ${labels.join(', ')} at ${time} — ${cron}` : 'Select at least one day.';
      }
      case 'monthly':
        return `Monthly on day ${this.fullRefreshDayOfMonthValue() || '1'} at ${time} — ${cron}`;
      default:
        return `Daily at ${time} — ${cron}`;
    }
  });

  protected readonly advancedOptionsOpen = signal(false);
  protected toggleAdvancedOptions(): void { this.advancedOptionsOpen.update(v => !v); }

  /** Incremental Sync is meaningless without the _lastUpdated cursor, so Run Mode = Incremental Sync forces it on and locks it. */
  protected readonly incrementalCursorLocked = computed(() =>
    this.retrievalMethod() === 'search-rest' && this.runModeValue() === 'incremental',
  );

  /** Full Refresh has no incremental filter, so it can pull the entire selected data set — worth a visible warning. */
  protected readonly showFullRefreshWarning = computed(() =>
    this.retrievalMethod() === 'search-rest' && this.runModeValue() === 'full',
  );

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

  /**
   * Epic rejects an unscoped Backend System Patient search outright ("This resource requires demographics or
   * _id parameter for searching" — business-rule 59159): with no SMART launch context and no discovered patient
   * cohort, FhirSourceConnectorBase.ApplyPatientScopeAsync has nothing to scope the request with unless this
   * connection's own Search Criteria supplies one. Search REST + Patient is the only combination where that gap
   * is guaranteed to hit Epic at runtime, so Search Criteria becomes required exactly there instead of staying
   * the generally-optional field it is for every other resource type / retrieval method.
   */
  protected readonly searchCriteriaRequiredForPatient = computed(() =>
    this.audience() === 'backend-system'
    && this.retrievalMethod() === 'search-rest'
    && this.searchRestResourceTypeValue().includes('Patient'));

  /** Section numbers shift depending on which optional sections the current audience shows. */
  protected readonly sectionNumbers = computed(() => {
    const cfg = this.audienceConfig();
    let n = 4; // 1 Audience/Env · 2 FHIR Base URL · 3 OAuth Endpoints · 4 Credentials
    const urls              = (this.showApplicationUrlsSection && (cfg.showLaunchUrl || cfg.showRedirect)) ? ++n : null;
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

  constructor() {
    // Keeps the "Private Key / JWKS URL" field itself correct for a Generated/Imported key, instead of only
    // showing the real URL in a toast — once resolvedSourceConnectionId() is known (after the first save, or
    // immediately when editing an already-saved connection), this is the one real, always-correct value; nothing
    // typed for a manual/external key is ever touched.
    effect(() => {
      const url = this.liveJwksUrl();
      if (url && (this.isGenKey() || this.isImportKey())) {
        this.form.controls.jwksUrl.setValue(url, { emitEvent: false });
      }
    });
  }

  ngOnInit(): void {
    // "New Source" (the default picker state) needs the same collision defense as "Existing Source" cloning —
    // otherwise a brand-new node left on the default App Name silently collides with a prior connection of that
    // exact name, and the raw backend "already exists" error only surfaces later, at workflow build time.
    if (this.showSourcePicker()) {
      this.loadAllConnectionNames();
    }

    if (this.wiz.isEditing()) {
      // Pre-populate all fields from saved node data
      this.form.controls.audience.setValue(this.wiz.epicAudience() as EpicAudience);
      this.form.controls.environment.setValue(this.wiz.env());
      this.form.controls.clientId.setValue(this.wiz.clientId());
      this.form.controls.authMethod.setValue(this.wiz.authMethod());
      this.form.controls.callbackUrl.setValue(this.wiz.redirectUri());
      this.form.controls.launchUrl.setValue(this.wiz.launchUrlWiz());
      this.restoreExtendedFieldsFromEditingNode();
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

    // New (non-editing) connections whose audience uses the shared Resource Type picker (removed from the UI —
    // see AUDIENCE_FIELD_CONFIG.showResourcePicker) always request every MVP1-supported resource type's scope
    // up front, rather than asking the user to hand-pick a subset before a destination even exists. Editing an
    // existing connection keeps whatever was actually saved (restored above), never overwritten here.
    if (!this.wiz.isEditing()
      && AUDIENCE_FIELD_CONFIG[this.audience()].showResourcePicker
      && this.form.controls.resources.value.length === 0) {
      this.form.controls.resources.setValue([...FHIR_RESOURCES]);
    }

    this.lockRetrievalMethodIfOneShot();
    this.syncValidators();

    this.form.controls.audience.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((next) => {
        const nextAudience = next as EpicAudience;
        this.clearInapplicableFields(this.prevAudience, nextAudience);
        this.prevAudience = nextAudience;
        this.lockRetrievalMethodIfOneShot();
        this.syncValidators();
      });

    this.form.controls.authMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncValidators());

    // Switching the base URL to/from a loopback address flips whether the OAuth/credential fields are required.
    this.form.controls.epicBaseUrl.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncValidators());

    this.form.controls.retrievalMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRetrievalValidators());

    this.form.controls.exportScope.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRetrievalValidators());

    // Toggling Patient in/out of Search REST's own Resource Type picker flips whether Search Criteria is
    // required (see searchCriteriaRequiredForPatient) — re-sync immediately rather than waiting for some other
    // field's valueChanges to happen to fire next.
    this.form.controls.searchRestResourceType.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRetrievalValidators());

    this.form.controls.runMode.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((mode) => {
        const cursorCtrl = this.form.controls.incrementalCursor;
        if (mode === 'incremental') {
          cursorCtrl.setValue(true);
          cursorCtrl.disable({ emitEvent: false });
        } else {
          cursorCtrl.enable({ emitEvent: false });
        }
        this.syncRetrievalValidators();
      });

    this.form.controls.fullRefreshRecurrence.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRetrievalValidators());

    // View mode (Source Connections page): lock every reactive-form control. Applied last so it wins over the
    // enable/disable calls the audience/runMode subscriptions above may have just issued during pre-population.
    if (this.isReadonly) {
      this.form.disable({ emitEvent: false });
    }
  }

  /**
   * WizardService.open() only restores the handful of fields it exposes as named signals (audience, environment,
   * clientId, authMethod, redirect/launch URL). Everything added since — JWT key material, CDS Hooks, scope
   * version, and the entire Data Retrieval Method section — has to be read back from the node's raw field bag
   * directly, using the exact same string keys save() writes. A missing key leaves the control at its form default,
   * so this is safe to call for a node saved before a given field existed.
   */
  private restoreExtendedFieldsFromEditingNode(): void {
    const fields = this.wiz.editingFields() ?? this.fieldsFromEntityDto();
    if (!fields) return;

    const setIfPresent = (control: string, key: string): void => {
      const value = fields[key];
      if (value !== undefined && value !== '') {
        this.form.get(control)?.setValue(value);
      }
    };

    setIfPresent('launchDisplayMode', 'Launch display mode');
    setIfPresent('jwksUrl', 'JWKS URL');
    setIfPresent('jwtKid', 'JWT kid');
    setIfPresent('privateKeyRef', 'Key vault reference');
    setIfPresent('privateKeySecretName', 'Secret Name');
    // Absent on any node saved before this control existed, and never present at all for entity mode (the DTO
    // carries no such field — see fieldsFromEntityDto()) — form default ('manual') is exactly right in both
    // cases, since a key restored with no known provenance can't safely be assumed Generated/Imported.
    setIfPresent('keySource', 'Signing key source');
    this.keyLoadedFromExistingConnection.set(
      !!(fields['JWT kid'] && fields['Key vault reference'] && fields['Secret Name'])
    );

    setIfPresent('cdsDiscoveryUrl', 'CDS discovery URL');
    setIfPresent('cdsServiceEndpoint', 'CDS service endpoint');
    setIfPresent('cdsTriggerHook', 'CDS trigger hook');
    setIfPresent('cdsReturnCard', 'CDS return card');
    setIfPresent('cdsDtrQuestionnaire', 'DTR questionnaire');

    if (fields['Scope version']?.startsWith('v1')) this.form.controls.scopeVersion.setValue('v1');
    else if (fields['Scope version']?.startsWith('v2')) this.form.controls.scopeVersion.setValue('v2');

    const retrievalMethod = fields['Retrieval method key'] as RetrievalMethod | undefined;
    if (!retrievalMethod || !RETRIEVAL_METHOD_CONFIG[retrievalMethod]) return;

    this.form.controls.retrievalMethod.setValue(retrievalMethod);

    // Each method owns its own Resource Type control; 'Retrieval resource type' is the one generic key save()
    // writes regardless of which method was active, so route it to the matching method's control.
    const methodCfg = RETRIEVAL_METHOD_CONFIG[retrievalMethod];
    const resourceField = methodCfg.fields.find(f => f.type === 'multiselect');
    if (resourceField && fields['Retrieval resource type']) {
      const types = fields['Retrieval resource type'].split(',').map(s => s.trim()).filter(Boolean);
      this.form.get(resourceField.key)?.setValue(types);
    }

    setIfPresent('eventType', 'Event type');
    setIfPresent('notificationPayload', 'Notification payload');
    setIfPresent('endpointType', 'Endpoint type');
    setIfPresent('reconciliationSchedule', 'Reconciliation schedule');
    setIfPresent('payloadFormat', 'Payload format');
    setIfPresent('searchCriteria', 'Search criteria');
    setIfPresent('schedulePollFrequency', 'Schedule / poll frequency');
    // The runMode valueChanges subscription that normally locks Incremental Sync isn't registered yet at this
    // point in ngOnInit, so replicate its effect explicitly rather than depending on subscription timing.
    if (fields['Run mode']) {
      this.form.controls.runMode.setValue(fields['Run mode']);
    }
    if (fields['Run mode'] === 'incremental') {
      this.form.controls.incrementalCursor.setValue(true);
      this.form.controls.incrementalCursor.disable({ emitEvent: false });
    } else if (fields['Incremental cursor']) {
      this.form.controls.incrementalCursor.setValue(fields['Incremental cursor'] === 'enabled');
    }
    setIfPresent('exportScope', 'Export scope');
    setIfPresent('groupId', 'Group ID');
    setIfPresent('patientIdList', 'Patient ID / list');
    setIfPresent('fhirOutputFormat', 'FHIR output format');

    setIfPresent('fullRefreshRecurrence', 'Full refresh recurrence');
    const daysOfWeek = fields['Full refresh days of week'];
    if (daysOfWeek) {
      this.form.controls.fullRefreshDaysOfWeek.setValue(daysOfWeek.split(',').map(s => s.trim()).filter(Boolean));
    }
    setIfPresent('fullRefreshDayOfMonth', 'Full refresh day of month');
    setIfPresent('fullRefreshTime', 'Full refresh time');

    setIfPresent('pageSize', 'Page size (_count)');
    setIfPresent('sortOrder', 'Sort (_sort)');
    setIfPresent('includeLinked', 'Include (_include)');
    setIfPresent('revIncludeLinked', 'Reverse include (_revinclude)');
    setIfPresent('retryPolicy', 'Retry policy');
    setIfPresent('timeoutSeconds', 'Timeout (seconds)');
    setIfPresent('maxRecordsPerRun', 'Max records per run');
  }

  /**
   * Entity mode (Settings > Source Connections) has no canvas node/fields-bag at all — editingFields() is
   * canvas-only and returns null here — so this synthesizes the same string-keyed shape restoreExtendedFieldsFrom-
   * EditingNode() already knows how to consume, straight from the real, persisted SourceConnectionModel DTO
   * (see WizardService.entityDto). CDS Hooks and Full Refresh recurrence have no equivalent below because the
   * backend SourceConnection genuinely doesn't persist them (same caveat WorkflowBuildAssemblerService's own doc
   * comment notes) — those controls simply keep their form defaults, same as a brand-new node would.
   */
  private fieldsFromEntityDto(): Record<string, string> | null {
    const dto = this.wiz.entityDto();
    if (!dto) return null;

    const auth = dto.authentication;
    const retrieval = dto.retrieval;
    const fields: Record<string, string> = {};

    if (auth?.keyId) fields['JWT kid'] = auth.keyId;
    if (auth?.privateKeyKeyVaultName) fields['Key vault reference'] = auth.privateKeyKeyVaultName;
    if (auth?.privateKeySecretName) fields['Secret Name'] = auth.privateKeySecretName;
    if (dto.interactive?.launchDisplayMode) fields['Launch display mode'] = dto.interactive.launchDisplayMode;

    if (retrieval) {
      fields['Retrieval method key'] = retrieval.retrievalMethod;
      if (retrieval.resourceTypes?.length) fields['Retrieval resource type'] = retrieval.resourceTypes.join(',');
      if (retrieval.searchCriteria) fields['Search criteria'] = retrieval.searchCriteria;
      fields['Incremental cursor'] = retrieval.incrementalSyncEnabled ? 'enabled' : 'disabled';
      if (retrieval.exportScope) fields['Export scope'] = retrieval.exportScope;
      if (retrieval.groupId) fields['Group ID'] = retrieval.groupId;
      if (retrieval.patientIds?.length) fields['Patient ID / list'] = retrieval.patientIds.join(', ');
      if (retrieval.outputFormat) fields['FHIR output format'] = retrieval.outputFormat;
      if (retrieval.pageSize != null) fields['Page size (_count)'] = String(retrieval.pageSize);
      if (retrieval.sortOrder) fields['Sort (_sort)'] = retrieval.sortOrder;
      if (retrieval.includeParameters?.length) fields['Include (_include)'] = retrieval.includeParameters.join(',');
      if (retrieval.revIncludeParameters?.length) fields['Reverse include (_revinclude)'] = retrieval.revIncludeParameters.join(',');
      if (retrieval.retryPolicy) fields['Retry policy'] = retrieval.retryPolicy;
      if (retrieval.timeoutSeconds != null) fields['Timeout (seconds)'] = String(retrieval.timeoutSeconds);
      if (retrieval.maxRecordsPerRun != null) fields['Max records per run'] = String(retrieval.maxRecordsPerRun);
    }

    return fields;
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

    if (prevCfg.showLaunchDisplayMode && !nextCfg.showLaunchDisplayMode) {
      this.form.patchValue({ launchDisplayMode: 'Embedded' });
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
    // Switching the other way: default to every MVP1-supported resource type's scope, same as the initial
    // load — there's no visible picker for the user to fill this in themselves anymore.
    if (!prevCfg.showResourcePicker && nextCfg.showResourcePicker) {
      this.form.patchValue({ resources: [...FHIR_RESOURCES] });
    }

    if (prevCfg.showRetrieval && !nextCfg.showRetrieval) {
      this.form.controls.incrementalCursor.enable({ emitEvent: false });
      this.form.patchValue({
        retrievalMethod: '',
        subscriptionResourceType: [], webhookResourceType: [], searchRestResourceType: [], bulkExportResourceType: [],
        eventType: '', notificationPayload: '',
        endpointType: '', reconciliationSchedule: '', payloadFormat: '', searchCriteria: '',
        incrementalCursor: false, schedulePollFrequency: '', runMode: '', exportScope: '',
        groupId: '', patientIdList: '', fhirOutputFormat: 'ndjson',
        fullRefreshRecurrence: 'daily', fullRefreshDaysOfWeek: [], fullRefreshDayOfMonth: '1', fullRefreshTime: '02:00',
        pageSize: '100', sortOrder: '', includeLinked: '', revIncludeLinked: '',
        retryPolicy: 'exponential', timeoutSeconds: '30', maxRecordsPerRun: '',
      });
    }

    // Narrowing from Backend System (automated) to Provider Standalone (one-shot): clear the automation-only
    // fields Standalone never shows (Run Mode, scheduler, incremental cursor, page size/sort/reverse-include/retry/
    // timeout) so stale values from a prior Backend System attempt in the same form session can't silently ride
    // along into the saved Standalone config. Search Criteria / Max Results / Include Related carry over — they're
    // meaningful for both scopes. searchRestResourceType is also cleared: Standalone reuses the shared Resource
    // Type & Scopes picker (Section 5) instead, so anything left in this hidden control would be dead data.
    if (prevCfg.retrievalScope === 'automated' && nextCfg.retrievalScope === 'oneshot') {
      this.form.controls.incrementalCursor.enable({ emitEvent: false });
      this.form.patchValue({
        searchRestResourceType: [],
        incrementalCursor: false, schedulePollFrequency: '', runMode: '',
        fullRefreshRecurrence: 'daily', fullRefreshDaysOfWeek: [], fullRefreshDayOfMonth: '1', fullRefreshTime: '02:00',
        pageSize: '100', sortOrder: '', revIncludeLinked: '',
        retryPolicy: 'exponential', timeoutSeconds: '30',
      });
    }
  }

  /** Standalone always uses Search REST — force-select it whenever the current audience is one-shot scoped, so
   *  the (hidden, for oneshot) method dropdown never leaves the form with no method chosen. */
  private lockRetrievalMethodIfOneShot(): void {
    if (this.audienceConfig().retrievalScope === 'oneshot' && this.form.controls.retrievalMethod.value !== 'search-rest') {
      this.form.controls.retrievalMethod.setValue('search-rest');
    }
  }

  /** True for a loopback FHIR base URL (localhost / 127.x / ::1) — a local HAPI dev source the backend treats as
   *  unauthenticated, so its OAuth/credential fields are optional in the wizard. */
  private isLoopbackUrl(value: string | null | undefined): boolean {
    if (!value) return false;
    try {
      const host = new URL(value).hostname.toLowerCase().replace(/^\[|\]$/g, '');
      return host === 'localhost' || host === '::1' || host === '127.0.0.1' || host.startsWith('127.');
    } catch {
      return false;
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

    // A loopback FHIR base URL (e.g. local HAPI at http://localhost:8080/fhir) is treated as unauthenticated by the
    // backend — it skips OAuth entirely — so the token/authorize endpoints and JWT key material aren't needed to
    // create or run it. Relax those here so a local HAPI source can be configured and tested straight from the UI.
    // Real (non-loopback) sources are unaffected: every credential field stays required exactly as before.
    const isLoopback = this.isLoopbackUrl(this.form.controls.epicBaseUrl.value);

    apply('epicBaseUrl',   true, true);
    apply('clientId',      true);
    apply('tokenEndpoint', !isLoopback, true);
    apply('authzEndpoint', !isLoopback, true);
    apply('callbackUrl',   cfg.showRedirect, true);
    apply('launchUrl',     cfg.showLaunchUrl, true);
    apply('resources',     cfg.showResourcePicker);
    apply('clientSecret',  method === 'secret');
    apply('jwksUrl',       method === 'jwt', true);
    // Epic Backend Services signs a JWT assertion with an RS384 private key referenced by (Key ID, Key Vault Name,
    // Secret Name) — ConfigurationService.ValidateEpicSourceConnection only requires all three in the non-interactive
    // (Backend System) branch; an EHR-launch/standalone/patient app can pick JWT client auth without them, so scope
    // the requirement to Backend System specifically rather than "JWT selected" generally.
    const requiresPrivateKeyReference = method === 'jwt' && this.audience() === 'backend-system' && !isLoopback;
    apply('jwtKid',               requiresPrivateKeyReference);
    apply('privateKeyRef',        requiresPrivateKeyReference);
    apply('privateKeySecretName', requiresPrivateKeyReference);

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
      let required = !!field && visibleKeys.has(key) && field.required;
      if (required && field?.requiredUnless && this.form.get(field.requiredUnless.key)?.value === field.requiredUnless.value) {
        required = false;
      }
      // Search Criteria is otherwise optional (see RETRIEVAL_METHOD_CONFIG['search-rest']) — Backend System
      // searching Patient is the one combination Epic guarantees to reject unscoped (see
      // searchCriteriaRequiredForPatient), so force it required there regardless of the static config.
      if (key === 'searchCriteria' && this.searchCriteriaRequiredForPatient()) {
        required = true;
      }
      apply(key, required);
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

  /** Each retrieval method owns its own Resource Type control — `key` picks which one. */
  protected toggleRetrievalResource(key: RetrievalFieldKey, r: string): void { this.toggleArrayControl(key, r); }
  protected toggleAllRetrievalResources(key: RetrievalFieldKey, all: readonly string[]): void { this.toggleAllArrayControl(key, all); }

  /** Weekday-picker field (Full Refresh recurrence) — same array-toggle mechanics as a resource-type multiselect.
   *  Reads the bridged signal (not `.value`) so the checked state stays reactive. */
  protected toggleWeekday(day: string): void { this.toggleArrayControl('fullRefreshDaysOfWeek', day); }
  protected readonly selectedWeekdays = computed(() => this.fullRefreshDaysOfWeekValue() ?? []);

  /** Generic invalid-and-touched check used by the config-driven retrieval field renderer. */
  protected isRetrievalFieldInvalid(field: RetrievalFieldDef): boolean {
    const ctrl = this.form.get(field.key);
    return !!ctrl && ctrl.touched && ctrl.invalid;
  }

  // ── New Source / Existing Source (canvas-mode create only) ─────────────────
  protected onSourceModeChange(mode: 'new' | 'existing'): void {
    this.sourceMode.set(mode);
    if (mode === 'new') {
      this.resetToBlankNewSource();
      return;
    }
    if (this.existingConnections().length === 0 && !this.loadingExisting()) {
      this.loadingExisting.set(true);
      this.sourceConnectionSvc.getAll().subscribe({
        next: connections => {
          this._allConnectionNames = new Set(connections.map(c => c.name));
          this.existingConnections.set(connections.filter(c => c.sourceSystemType === 'Epic'));
          this.loadingExisting.set(false);
        },
        error: () => {
          this.loadingExisting.set(false);
          this.toast.show('Failed to load', 'Could not load existing Epic source connections.', 'error');
        },
      });
    }
  }

  /** Best-effort load of every saved connection's name so save()'s "New Source" branch can dedupe against real
   *  collisions up front — a failed load just skips the client-side check, since the backend still rejects a
   *  true collision regardless. */
  private loadAllConnectionNames(): void {
    // Positional (next-only) subscribe: a failed load has nothing to react to (see doc comment above), so there's
    // no error callback to keep empty — RxJS's default unhandled-error reporting is fine for a best-effort read.
    this.sourceConnectionSvc.getAll().subscribe(
      connections => this._allConnectionNames = new Set(connections.map(c => c.name)),
    );
  }

  /**
   * Resolves a unique name for the cloned connection. When the user typed their own distinct name, it's used
   * as-is (forceSuffix false) — only a genuine collision gets a -1/-2/... suffix appended. When the name was left
   * as whatever was cloned in (forceSuffix true), a suffix is always appended, since the original connection
   * already holds that exact name.
   */
  private _resolveUniqueSourceName(desiredName: string, forceSuffix: boolean): string {
    if (!forceSuffix && !this._allConnectionNames.has(desiredName)) return desiredName;
    let suffix = 1;
    let candidate = `${desiredName}-${suffix}`;
    while (this._allConnectionNames.has(candidate)) {
      suffix++;
      candidate = `${desiredName}-${suffix}`;
    }
    return candidate;
  }

  /** Switching back to "New Source" after a clone must undo it — otherwise the form silently keeps whatever
   *  the last-selected existing connection populated, even though the picker now reads "New Source". */
  private resetToBlankNewSource(): void {
    this.selectedExistingId.set(null);
    this._existingBaseline = null;
    this._awaitingBaselineSnapshot = false;
    this.form.reset();
    this.discoveredResourceTypes.set([]);
    this.discStatus.set('idle');
    this.discValues.set(null);
    this.discoveredScopes.set([]);
    this.scopeVersionAuto.set(false);
    this.authMethodAuto.set(false);
    this.testStatus.set('idle');
    this.wiz.trustedIssuers.set('');

    this.prevAudience = this.audience();
    this.lockRetrievalMethodIfOneShot();
    this.syncValidators();
    this.syncRetrievalValidators();
  }

  protected onExistingConnectionSelected(id: string): void {
    this.selectedExistingId.set(id);
    const dto = this.existingConnections().find(c => c.id === id);
    if (dto) this.populateFormFromSourceConnection(dto);
  }

  /** Generates a real RSA key pair server-side (SigningKeyGenerationService) and stores the private key in the
   *  secret store — no key material ever reaches this component. Populates jwtKid/privateKeyRef/
   *  privateKeySecretName exactly as a manually-entered key would, so save()/syncValidators() need no special
   *  casing for how the key was provisioned. */
  protected generateKeyPair(): void {
    this.keyGenStatus.set('generating');
    this.sourceConnectionSvc.generateSigningKey().subscribe({
      next: (key) => {
        this.form.patchValue({
          jwtKid: key.keyId,
          privateKeyRef: key.keyVaultName,
          privateKeySecretName: key.secretName,
        });
        this.keyGenStatus.set('generated');
        this.toast.show(
          'Key pair generated',
          `Key ID ${key.keyId} generated. The JWKS URL becomes available at this connection's ` +
          '.well-known/jwks.json once you save — register that URL in Epic.'
        );
      },
      error: (err) => {
        this.keyGenStatus.set('idle');
        const message = err?.error?.message ?? err?.error?.title ?? 'Failed to generate a key pair.';
        this.toast.show('Key generation failed', message, 'error');
      },
    });
  }

  /** Reads the chosen file client-side only — nothing is sent to the backend until "Import Private Key" is
   *  clicked, so picking the wrong file and re-picking costs nothing. */
  protected onPrivateKeyFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.pendingPrivateKeyPem = null;
    this.keyGenStatus.set('idle');
    this.selectedFileName.set(file.name);

    const reader = new FileReader();
    reader.onload = () => {
      this.pendingPrivateKeyPem = reader.result as string;
    };
    reader.onerror = () => {
      this.selectedFileName.set(null);
      this.toast.show('Could not read file', 'Choose the file again.', 'error');
    };
    reader.readAsText(file);
  }

  /** Sends the selected file's contents to the backend, which validates it's a real, unencrypted RSA private key
   *  (rejecting a public key, certificate, non-RSA key, or a weak/encrypted one) before storing it — the same
   *  secret-store path generateKeyPair() uses, so jwtKid/privateKeyRef/privateKeySecretName are wired up
   *  identically either way. */
  protected importPrivateKey(): void {
    if (!this.pendingPrivateKeyPem) {
      this.toast.show('Choose a file', 'Choose your private key file before importing.');
      return;
    }

    this.keyGenStatus.set('generating');
    this.sourceConnectionSvc.importSigningKey(this.pendingPrivateKeyPem).subscribe({
      next: (key) => {
        this.form.patchValue({
          jwtKid: key.keyId,
          privateKeyRef: key.keyVaultName,
          privateKeySecretName: key.secretName,
        });
        this.keyGenStatus.set('generated');
        this.toast.show(
          'Private key imported',
          `Key ID ${key.keyId} imported. The JWKS URL becomes available at this connection's ` +
          '.well-known/jwks.json once you save — register that URL in Epic.'
        );
      },
      error: (err) => {
        this.keyGenStatus.set('idle');
        const message = err?.error?.message ?? err?.error?.title ?? 'Failed to import the private key.';
        this.toast.show('Import failed', message, 'error');
      },
    });
  }

  /** Name + Base URL alone can collide (e.g. two connections both literally named "Epic" against the same
   *  sandbox URL, one EHR Launch and one Standalone) — append audience + a Client ID suffix so the dropdown
   *  always has something to visually tell them apart by. */
  protected existingConnectionLabel(conn: SourceConnectionModel): string {
    const audience = (conn.applicationType && APPLICATION_TYPE_TO_AUDIENCE[conn.applicationType]) || null;
    const audienceLabel = audience ? ` · ${audience}` : '';
    const clientIdSuffix = conn.authentication?.clientId ? ` · …${conn.authentication.clientId.slice(-6)}` : '';
    return `${conn.name} — ${conn.baseUrl}${audienceLabel}${clientIdSuffix}`;
  }

  /**
   * Clones a persisted SourceConnection's data into this (still-unsaved) canvas node's form — lets a new
   * pipeline node start from an already-configured connection instead of re-entering everything by hand.
   * Mirrors WizardService.openEntity()'s field mapping, but patches the reactive form directly: this is canvas
   * mode, so there's no backend entity id to attach to — the cloned values just become this new node's own,
   * independently editable, canvas fields once Save is clicked.
   */
  private populateFormFromSourceConnection(dto: SourceConnectionModel): void {
    const audience = ((dto.applicationType && APPLICATION_TYPE_TO_AUDIENCE[dto.applicationType])
      || 'provider-ehr-launch') as EpicAudience;
    const authMethod = AUTHENTICATION_TYPE_TO_AUTH_METHOD[dto.authentication?.authenticationType ?? 'None'] ?? 'secret';
    const retrieval = dto.retrieval;

    const retrievalResourceKeyByMethod: Record<string, string> = {
      subscription: 'subscriptionResourceType',
      webhook: 'webhookResourceType',
      'search-rest': 'searchRestResourceType',
      'bulk-export': 'bulkExportResourceType',
    };
    const retrievalResourceKey = retrieval ? retrievalResourceKeyByMethod[retrieval.retrievalMethod] : undefined;

    this.form.patchValue({
      audience,
      environment: 'sandbox',
      appName: dto.name,
      epicBaseUrl: dto.baseUrl,
      tokenEndpoint: dto.authentication?.tokenEndpoint ?? '',
      clientId: dto.authentication?.clientId ?? '',
      authMethod,
      jwtKid: dto.authentication?.keyId ?? '',
      privateKeyRef: dto.authentication?.privateKeyKeyVaultName ?? '',
      privateKeySecretName: dto.authentication?.privateKeySecretName ?? '',
      // Required for EHR-Launch/Standalone audiences, but neither is guaranteed to be persisted on the source
      // connection being cloned (e.g. LaunchUrl is NULL on plenty of real rows, and EHR-Launch connections never
      // persist a resource-type selection server-side at all) — fall back to whatever the form already holds
      // (ngOnInit's own sensible defaults) rather than blanking a required field out and silently failing Save.
      launchUrl: dto.interactive?.launchUrl || this.form.controls.launchUrl.value,
      callbackUrl: dto.interactive?.redirectUris?.[0] ?? this.form.controls.callbackUrl.value,
      resources: retrieval?.resourceTypes?.length ? [...retrieval.resourceTypes] : this.form.controls.resources.value,
      retrievalMethod: (retrieval?.retrievalMethod as RetrievalMethod) ?? '',
      searchCriteria: retrieval?.searchCriteria ?? '',
      incrementalCursor: retrieval?.incrementalSyncEnabled ?? false,
      pageSize: retrieval?.pageSize != null ? String(retrieval.pageSize) : this.form.controls.pageSize.value,
      sortOrder: retrieval?.sortOrder ?? '',
      includeLinked: retrieval?.includeParameters?.join(',') ?? '',
      revIncludeLinked: retrieval?.revIncludeParameters?.join(',') ?? '',
      retryPolicy: retrieval?.retryPolicy ?? this.form.controls.retryPolicy.value,
      timeoutSeconds: retrieval?.timeoutSeconds != null ? String(retrieval.timeoutSeconds) : this.form.controls.timeoutSeconds.value,
      maxRecordsPerRun: retrieval?.maxRecordsPerRun != null ? String(retrieval.maxRecordsPerRun) : '',
      exportScope: retrieval?.exportScope ?? '',
      groupId: retrieval?.groupId ?? '',
      patientIdList: retrieval?.patientIds?.join(', ') ?? '',
      fhirOutputFormat: retrieval?.outputFormat ?? this.form.controls.fhirOutputFormat.value,
    });

    if (retrievalResourceKey && retrieval?.resourceTypes?.length) {
      this.form.get(retrievalResourceKey)?.setValue([...retrieval.resourceTypes]);
    }

    // Cloned key material still deserves the "already configured, confirm before replacing" guard — clicking
    // Generate/Import here would fork a brand-new SourceConnection on save (this is a clone into a new node, not
    // an edit-in-place), but the point of the guard is preventing an accidental click from discarding key info
    // that took effort to have populated, which applies just as much to cloned data as to the original entity's.
    this.keyLoadedFromExistingConnection.set(
      !!(dto.authentication?.keyId && dto.authentication?.privateKeyKeyVaultName && dto.authentication?.privateKeySecretName)
    );

    this.wiz.trustedIssuers.set(dto.interactive?.trustedIssuers?.join(', ') ?? '');
    this.discoveredResourceTypes.set(retrieval?.resourceTypes?.length ? [...retrieval.resourceTypes] : []);

    this.prevAudience = audience;
    this.lockRetrievalMethodIfOneShot();
    this.syncValidators();
    this.syncRetrievalValidators();

    this.toast.show('Loaded', `Copied configuration from "${dto.name}".`);

    // SourceConnection only ever persists a Token Endpoint — there's no Authorization Endpoint column at all
    // (nothing to clone it from) — so run real SMART discovery against the cloned Base URL instead of faking
    // discStatus 'done'/an "Auto-populated" badge for data that was never actually saved. This also re-confirms
    // Token Endpoint live and refreshes the discovered (available) resource type list for this source right now.
    // The baseline snapshot (for hasExistingChanged) is taken once this settles, not here — see runDiscover().
    this._awaitingBaselineSnapshot = true;
    this.runDiscover();
  }

  /** True once the user has edited any field away from what populateFormFromSourceConnection() cloned in (as
   *  re-confirmed by discovery) — the save-time signal for "fork a new connection" vs "reuse this one untouched"
   *  (see save()). clientSecret is excluded on both sides: it's never populated by the clone (secrets never come
   *  back from the API), so typing one in to satisfy validation must not by itself count as "changed". */
  protected hasExistingChanged(): boolean {
    if (!this._existingBaseline) return false;
    const strip = (v: Record<string, unknown>) =>
      Object.fromEntries(Object.entries(v).filter(([key]) => key !== 'clientSecret'));
    return JSON.stringify(strip(this.form.getRawValue())) !== JSON.stringify(strip(this._existingBaseline));
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
        // A plain (non-SMART) FHIR R4 server has no discovered endpoints — don't stomp the Token/Authorization
        // Endpoint fields with empty strings; leave them for manual entry (and don't overwrite a manual entry the
        // user already typed if Discover is re-run).
        if (!result.smartConfigurationError) {
          this.form.controls.tokenEndpoint.setValue(dv.tokenEndpoint);
          this.form.controls.authzEndpoint.setValue(dv.authzEndpoint);
          this.wiz.token.set(dv.tokenEndpoint);
          this.wiz.authorize.set(dv.authzEndpoint);
        }
        this.wiz.baseUrl.set(url);
        this.wiz.setDiscovered(true);
        this.discStatus.set('done');
        if (result.smartConfigurationError) {
          this.toast.show(
            'No SMART discovery found',
            `This server has no /.well-known/smart-configuration (plain FHIR R4 — e.g. a local HAPI test server). Enter Token/Authorization Endpoint manually. Found ${result.resourceTypes.length} resource type(s) from /metadata.`,
            'warning',
          );
        } else if (result.resourceTypesError) {
          this.toast.show('Discovery complete (partial)', `Endpoints resolved. Resource types unavailable: ${result.resourceTypesError}`);
        } else {
          this.toast.show('Discovery complete', `Resolved endpoints + ${result.resourceTypes.length} resource types.`);
        }
        this._captureBaselineIfAwaiting();
      },
      error: (err) => {
        this.discStatus.set('error');
        const msg = typeof err?.error?.error === 'string' ? err.error.error : 'Check the URL or enter endpoints manually.';
        this.toast.show('Discovery failed', msg);
        this._captureBaselineIfAwaiting();
      },
    });
  }

  /** Captures the hasExistingChanged() baseline once discovery settles after a clone — see the
   *  _awaitingBaselineSnapshot comment on populateFormFromSourceConnection(). No-op outside that flow. */
  private _captureBaselineIfAwaiting(): void {
    if (!this._awaitingBaselineSnapshot) return;
    this._awaitingBaselineSnapshot = false;
    this._existingBaseline = this.form.getRawValue();
  }

  protected runTestConnection(): void {
    const baseUrl = this.form.controls.epicBaseUrl.value.trim();
    if (!baseUrl) { this.toast.show('Base URL required', 'Enter the Epic FHIR base URL first.'); return; }
    this.testStatus.set('running');
    // Real reachability test: probe the source's public SMART/metadata endpoints via the backend. Both probes are
    // best-effort server-side, so this only fails on network/URL errors, not a missing smart-configuration document.
    this.discovery.discover(baseUrl).subscribe({
      next: (result) => {
        this.testStatus.set('ok');
        this.toast.show(
          'Test passed',
          result.smartConfigurationError
            ? 'Reached the FHIR endpoint (no SMART configuration document — plain FHIR R4 server).'
            : 'Reached the source SMART configuration endpoint.',
        );
      },
      error: (err) => {
        this.testStatus.set('fail');
        const msg = typeof err?.error?.error === 'string' ? err.error.error : 'Could not reach the source endpoint.';
        this.toast.show('Test failed', msg);
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
    // getRawValue (not .value) so the Incremental Sync checkbox is included even while
    // locked/disabled by Run Mode = Incremental Sync (disabled controls are omitted from .value).
    const v = this.form.getRawValue();

    const aud      = v.audience as EpicAudience;
    const envKey: EnvKey = v.environment === 'production' ? 'production' : 'sandbox';
    const appKeyMap: Record<EpicAudience, AppKey> = {
      'provider-ehr-launch': 'provider-ehr-launch',
      'provider-standalone': 'provider-standalone',
      'backend-system':      'backend-system',
      'patient':             'patient-standalone',
    };

    const cfg = this.audienceConfig();

    // Emit the calendar recurrence + compiled cron for anything that schedules on it: Search-REST Full Refresh, and
    // a System/Group bulk export (a Patient-id-list export is a one-off and stays manual). buildTrigger() in the
    // workflow builder reads 'Full refresh schedule (cron)' to compile the workflow's Schedule trigger.
    const emitRecurrence = v.runMode === 'full'
      || (this.retrievalMethod() === 'bulk-export' && v.exportScope !== '' && v.exportScope !== 'patient');

    // "Existing Source" has two outcomes depending on whether the form still matches what
    // populateFormFromSourceConnection() cloned in (as re-confirmed by discovery):
    //  - Untouched: wire the node straight to the already-saved connection (its real id) —
    //    resolvedSourceConnectionId tells workflow-build-assembler.service.ts to skip this node
    //    entirely, so a connection another workflow also points at can never be mutated by this save.
    //  - Edited: fork it as a new, independent connection. If the user left Name exactly as cloned,
    //    it collides with the original unless suffixed; if they typed their own distinct name, honor
    //    it as-is (only deduped on an actual collision) rather than silently suffixing a chosen name.
    let resolvedName = v.appName ?? 'Epic';
    let resolvedSourceConnectionId: string | null = null;
    if (this.sourceMode() === 'existing') {
      const original = this.existingConnections().find(c => c.id === this.selectedExistingId());
      if (original && !this.hasExistingChanged()) {
        resolvedName = original.name;
        resolvedSourceConnectionId = original.id;
      } else {
        const nameWasEdited = !!original && resolvedName !== original.name;
        resolvedName = this._resolveUniqueSourceName(resolvedName, !nameWasEdited);
      }
    } else {
      // "New Source": a brand-new node left on the default (or a reused) App Name must not silently collide with
      // an already-persisted connection of that exact name — only suffix on an actual collision (forceSuffix
      // false), so a genuinely unique name the user typed is still honored as-is. No-ops in entity-mode create,
      // where _allConnectionNames is never populated (see loadAllConnectionNames's showSourcePicker guard).
      resolvedName = this._resolveUniqueSourceName(resolvedName, false);
    }

    this.wiz.setAppKey(appKeyMap[aud]);
    this.wiz.setEnv(envKey);
    this.wiz.stepName.set(resolvedName);
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
      stepName:    resolvedName,
      baseUrl:     v.epicBaseUrl ?? '',
      token:       v.tokenEndpoint ?? '',
      authorize:   v.authzEndpoint ?? '',
      algorithm:   'RS384',
      jwksMethod:  v.authMethod === 'jwt' ? 'hosted' : 'external',
      jwksUrl:     v.jwksUrl ?? '',
      kid:         v.jwtKid ?? '',
      kvRef:       v.privateKeyRef ?? '',
      secretName:  v.privateKeySecretName ?? '',
      redirectUri: v.callbackUrl ?? '',
      launchUrl:   v.launchUrl ?? '',
    }, {
      'Client ID':             v.clientId ?? '',
      'Auth method':           v.authMethod ?? 'secret',
      // Only meaningful for Backend System + JWT — lets a later "was this key FHIRBridge-provisioned?" check (e.g.
      // WorkflowBuilderComponent auto-filling the real JWKS URL after build assigns a sourceConnectionId) tell a
      // generated/imported key apart from one pointing at an externally-hosted JWKS, without re-deriving it from
      // the Key Vault Name/Secret Name values alone (which look identical either way).
      'Signing key source':    v.keySource ?? 'manual',
      'Epic audience':         aud,
      'SMART version':         'SMART App Launch 2.0 (R4)',
      'Scope version':         v.scopeVersion === 'v1' ? 'v1 (coarse)' : 'v2 (granular)',
      // The actual, discovery-validated scope string this form built and showed the user — takes priority over
      // WizardService.save()'s own ScopeBuilderService-derived default (which knows nothing about the selected
      // resources, scope version, or audience-specific scopes this form computed).
      'Scopes':                this.scopeString().split(/\s+/).filter(Boolean).join(' '),
      // Only meaningful for EHR launch — cleared to '' otherwise so it's never wired into the build request
      // (see WorkflowBuildAssemblerService.buildSource, which only reads this key for the EhrLaunch application type).
      'Launch display mode':   cfg.showLaunchDisplayMode ? (v.launchDisplayMode ?? 'Embedded') : '',
      'CDS discovery URL':     v.cdsDiscoveryUrl ?? '',
      'CDS service endpoint':  v.cdsServiceEndpoint ?? '',
      'CDS trigger hook':      v.cdsTriggerHook ?? '',
      'CDS return card':       v.cdsReturnCard ?? '',
      'DTR questionnaire':     v.cdsDtrQuestionnaire ?? '',
      ...(cfg.showRetrieval ? {
        'Data retrieval method':     this.retrievalConfig()?.label ?? '',
        // Machine-readable retrieval method key (e.g. "search-rest"), distinct from the display label above —
        // consumed by WorkflowBuildAssemblerService to build the CreateSourceConnectionRequest.retrieval payload.
        'Retrieval method key':      this.retrievalMethod(),
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
        // ── Calendar recurrence (Search-REST Full Refresh, or System/Group bulk export) ──
        ...(emitRecurrence ? {
          'Full refresh recurrence':    v.fullRefreshRecurrence ?? 'daily',
          'Full refresh days of week':  (v.fullRefreshDaysOfWeek ?? []).join(','),
          'Full refresh day of month':  v.fullRefreshDayOfMonth ?? '1',
          'Full refresh time':          v.fullRefreshTime ?? '02:00',
          'Full refresh schedule (cron)': this.fullRefreshCronExpression() ?? '',
        } : {}),
        // ── Advanced Search Options ──────────────────────────────────────────
        'Page size (_count)':        v.pageSize ?? '',
        'Sort (_sort)':              v.sortOrder ?? '',
        'Include (_include)':       v.includeLinked ?? '',
        'Reverse include (_revinclude)': v.revIncludeLinked ?? '',
        'Retry policy':              v.retryPolicy ?? '',
        'Timeout (seconds)':         v.timeoutSeconds ?? '',
        'Max records per run':       v.maxRecordsPerRun ?? '',
      } : {}),
      // Reused-as-is "Existing Source" pick (see resolvedSourceConnectionId above): tells
      // WorkflowBuildAssemblerService.assemble() to skip this node's Sources spec entirely and let the backend
      // resolve sourceConnectionId straight off this node's own config, same as destinationResolved for destinations.
      ...(resolvedSourceConnectionId ? {
        sourceConnectionId: resolvedSourceConnectionId,
        sourceConnectionResolved: 'true',
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
