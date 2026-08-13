import {
  Component, input, output, inject, signal, computed, effect, OnInit, DestroyRef, ElementRef, ViewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { take } from 'rxjs/operators';
import { ReactiveFormsModule, FormBuilder, Validators, ValidatorFn, AbstractControl, ValidationErrors } from '@angular/forms';
import { WizardService, APPLICATION_TYPE_TO_AUDIENCE, AUTHENTICATION_TYPE_TO_AUTH_METHOD } from '../../../services/wizard.service';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { ToastService } from '../../../services/toast.service';
import { EPIC_ENV } from '../../../data/epic-environments.data';
import { EnvKey } from '../../../models/epic-env.model';
import { AppKey } from '../../../models/epic-app.model';
import { FullDiscoveredValues } from '../../epic-source-wizard/models/epic-config.model';
import { EpicAudience, AudienceFieldConfig, AUDIENCE_FIELD_CONFIG, isAudienceDisabledForVendor } from '../../epic-source-wizard/models/audience-field-config.data';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { SUPPORTED_RESOURCE_TYPES } from '../../../data/scope-constants.data';
import { environment } from '../../../../environments/environment';
import { OAUTH_DEFAULT_URLS } from '../../../core/api-endpoints';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { SourceConfigFormComponent } from '../config-form/config-form.contract';

export type { EpicAudience };

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
/**
 * `token_endpoint_auth_methods_supported` is a server-wide list (every method the FHIR server accepts from any
 * client), not a statement about how *this* app is registered — Epic's discovery document lists
 * client_secret_basic/post and private_key_jwt for essentially every environment regardless of whether a given app
 * is public or confidential. So it can only drive an auto-selection where the SMART flow itself mandates one method:
 * Epic's Backend Services (system/backend-system) profile is JWT-only per spec, so that's the one case we can
 * safely auto-select for Epic specifically. athenahealth's Backend audience is plain OAuth2 client_credentials with
 * a client secret (BackendServicesApplicationStrategy dispatches by which credential the connection actually
 * carries — see the backend) — its discovery document listing private_key_jwt as a server-wide *capability* does
 * NOT mean this app is registered that way, so the same inference would silently stomp a real Client Secret
 * connection with JWT (exactly the bug this vendor check fixes). Interactive audiences (EHR launch / standalone /
 * patient), and every non-Epic vendor's Backend audience, keep whatever the user/connection actually has.
 */
function detectAuthMethod(vendor: EhrVendor, audience: EpicAudience, authMethodsSupported: string[]): 'public' | 'secret' | 'jwt' | null {
  if (audience !== 'backend-system' || vendor !== 'Epic') return null;
  const methods = authMethodsSupported.map(m => m.toLowerCase());
  return methods.includes('private_key_jwt') ? 'jwt' : null;
}

/** Client Auth Method default per audience, applied on every audience switch (see the `audience.valueChanges`
 *  subscription): every interactive audience (EHR launch / standalone / patient) is Public Client + PKCE for every
 *  vendor. Backend System defaults to JWT for Epic (SMART Backend Services, private_key_jwt) but Client Secret for
 *  athenahealth (plain OAuth2 client_credentials) — see detectAuthMethod's remarks for why vendor matters here. */
function defaultAuthMethodFor(vendor: EhrVendor, audience: EpicAudience): 'public' | 'secret' | 'jwt' {
  if (audience !== 'backend-system') return 'public';
  return vendor === 'Athenahealth' ? 'secret' : 'jwt';
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
  | 'fullRefreshRecurrence' | 'fullRefreshDaysOfWeek' | 'fullRefreshDayOfMonth' | 'fullRefreshTime' | 'fullRefreshTimeZone';

const RETRIEVAL_FIELD_KEYS: readonly RetrievalFieldKey[] = [
  'subscriptionResourceType', 'webhookResourceType', 'searchRestResourceType', 'bulkExportResourceType',
  'eventType', 'notificationPayload', 'endpointType', 'reconciliationSchedule',
  'payloadFormat', 'searchCriteria', 'incrementalCursor', 'schedulePollFrequency', 'runMode',
  'exportScope', 'groupId', 'patientIdList', 'fhirOutputFormat',
  'pageSize', 'sortOrder', 'includeLinked', 'revIncludeLinked', 'retryPolicy', 'timeoutSeconds', 'maxRecordsPerRun',
  'fullRefreshRecurrence', 'fullRefreshDaysOfWeek', 'fullRefreshDayOfMonth', 'fullRefreshTime', 'fullRefreshTimeZone',
];

/** Browser's own zone (e.g. "America/New_York") — used as the schedule time zone picker's default. Exported so
 *  other retrieval-config forms (e.g. GenericFhirSourceFormComponent) sharing this same calendar-recurrence
 *  scheduling model don't need their own copy. */
export function detectBrowserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** All IANA zone identifiers the runtime knows about, for the "Time zone" select. Falls back to a short curated
 *  list on engines without `Intl.supportedValuesOf` (older Safari/older browsers not in FHIRBridge's support matrix
 *  but cheap to guard against). */
export const TIME_ZONE_OPTIONS: readonly RetrievalFieldOption[] = (() => {
  const zones: string[] = typeof Intl.supportedValuesOf === 'function'
    ? Intl.supportedValuesOf('timeZone')
    : ['UTC', 'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles', 'Europe/London'];
  return zones.map(zone => ({ value: zone, label: zone.replace(/_/g, ' ') }));
})();

/** Blank/default value for each retrieval field, matching the form's own initial values — used to clear a field
 *  out when switching Retrieval Method away from the method that owns it (see clearInapplicableRetrievalFields). */
const RETRIEVAL_FIELD_DEFAULTS: Record<RetrievalFieldKey, unknown> = {
  subscriptionResourceType: [] as string[],
  webhookResourceType:      [] as string[],
  searchRestResourceType:   [] as string[],
  bulkExportResourceType:   [] as string[],
  eventType:              '',
  notificationPayload:    '',
  endpointType:           '',
  reconciliationSchedule: '',
  payloadFormat:          '',
  searchCriteria:         '',
  incrementalCursor:      false,
  schedulePollFrequency:  '',
  runMode:                '',
  exportScope:            '',
  groupId:                '',
  patientIdList:          '',
  fhirOutputFormat:       'ndjson',
  fullRefreshRecurrence:  'daily',
  fullRefreshDaysOfWeek:  [] as string[],
  fullRefreshDayOfMonth:  '1',
  fullRefreshTime:        '02:00',
  fullRefreshTimeZone:    '',
  pageSize:               '100',
  sortOrder:              '',
  includeLinked:          '',
  revIncludeLinked:       '',
  retryPolicy:            'exponential',
  timeoutSeconds:         '30',
  maxRecordsPerRun:       '',
};

export interface RetrievalFieldOption { value: string; label: string; }

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

export const POLL_FREQUENCY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '5m',  label: 'Every 5 minutes' },
  { value: '15m', label: 'Every 15 minutes' },
  { value: '30m', label: 'Every 30 minutes' },
  { value: '1h',  label: 'Hourly' },
  { value: '1d',  label: 'Daily' },
];

export const RECONCILIATION_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'none', label: 'Disabled' },
  { value: '1h',   label: 'Hourly' },
  { value: '6h',   label: 'Every 6 hours' },
  { value: '1d',   label: 'Daily' },
  { value: '1w',   label: 'Weekly' },
];

export const ENDPOINT_TYPE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'rest-hook', label: 'REST Hook (HTTPS callback)' },
  { value: 'websocket', label: 'WebSocket' },
  { value: 'mllp',      label: 'MLLP (HL7 v2)' },
];

export const EVENT_TYPE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'created',            label: 'Record created' },
  { value: 'updated',            label: 'Record updated' },
  { value: 'created-or-updated', label: 'Created or updated' },
  { value: 'deleted',            label: 'Record deleted' },
];

export const SORT_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '_lastUpdated',  label: '_lastUpdated (oldest → newest)' },
  { value: '-_lastUpdated', label: '_lastUpdated (newest → oldest)' },
  { value: 'date',          label: 'date (ascending)' },
  { value: '-date',         label: 'date (descending)' },
];

export const RETRY_POLICY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'none',        label: 'No retry' },
  { value: 'fixed-3',     label: 'Fixed — 3 attempts' },
  { value: 'exponential', label: 'Exponential backoff' },
];

// ── Full Refresh calendar recurrence (Google Calendar-style: anchored to a specific time, not an interval) ──────
export const FULL_REFRESH_RECURRENCE_OPTIONS: readonly RetrievalFieldOption[] = [
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
      // Hidden here for the same reason as searchRestResourceType below (Resource Type is already captured by the
      // destination node's own "dest_resources" picker) — the control itself stays in the form: scope generation
      // (activeRetrievalResourceTypes/scopeString) still reads it. See ensureRetrievalResourceTypeDefault.
      { key: 'subscriptionResourceType', label: 'Resource Type',        type: 'multiselect', required: false, visibleWhen: () => false },
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
      // Hidden — see subscriptionResourceType above / ensureRetrievalResourceTypeDefault.
      { key: 'webhookResourceType',    label: 'Resource Type',           type: 'multiselect', required: false, visibleWhen: () => false },
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
      // Hidden for every retrieval scope (Standalone already reused the shared Resource Type & Scopes picker
      // instead; Backend System now gets the same full-MVP1-set default automatically — see
      // ensureRetrievalResourceTypeDefault — rather than a second, separate multiselect). The control itself
      // stays in the form: scope generation (activeRetrievalResourceTypes/scopeString) still reads it.
      { key: 'searchRestResourceType', label: 'Resource Type',                   type: 'multiselect', required: false, visibleWhen: () => false },
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
      { key: 'schedulePollFrequency', label: 'Schedule / Poll Frequency',        type: 'select',       required: true, options: POLL_FREQUENCY_OPTIONS, requiredUnless: { key: 'runMode', value: 'manual' }, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode !== 'full', hint: 'Not required when Run Mode is Manual Only — the workflow only runs when triggered.' },
      { key: 'fullRefreshRecurrence',  label: 'Repeat',                           type: 'select',       required: true, options: FULL_REFRESH_RECURRENCE_OPTIONS, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full', hint: 'Full Refresh reloads everything with no incremental filter — anchor it to a specific, low-traffic time rather than a tight interval.' },
      { key: 'fullRefreshDaysOfWeek', label: 'On',                               type: 'weekday-picker', required: true, options: WEEKDAY_OPTIONS, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full' && ctx.fullRefreshRecurrence === 'weekly' },
      { key: 'fullRefreshDayOfMonth', label: 'Day of month',                     type: 'select',       required: true, options: Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: `${i + 1}` })), visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full' && ctx.fullRefreshRecurrence === 'monthly', hint: 'Capped at 28 so it fires every month, including February.' },
      { key: 'fullRefreshTime',       label: 'At',                               type: 'time',          required: true, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full', hint: 'Runs in the time zone selected below. Pick an off-hours slot to avoid contending with interactive EHR traffic.' },
      { key: 'fullRefreshTimeZone',   label: 'Time zone',                        type: 'select',       required: true, options: TIME_ZONE_OPTIONS, visibleWhen: ctx => ctx.retrievalScope === 'automated' && ctx.runMode === 'full', hint: 'The schedule above is evaluated in this time zone, including daylight saving transitions.' },
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
      // Hidden — see subscriptionResourceType above / ensureRetrievalResourceTypeDefault.
      { key: 'bulkExportResourceType', label: 'Resource Type',               type: 'multiselect', required: false, visibleWhen: () => false },
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
      { key: 'fullRefreshTime',       label: 'At',            type: 'time',           required: true, visibleWhen: ctx => ctx.exportScope !== '' && ctx.exportScope !== 'patient', hint: 'Runs in the time zone selected below. Pick an off-hours slot to avoid contending with interactive EHR traffic.' },
      { key: 'fullRefreshTimeZone',   label: 'Time zone',     type: 'select',         required: true, options: TIME_ZONE_OPTIONS, visibleWhen: ctx => ctx.exportScope !== '' && ctx.exportScope !== 'patient', hint: 'The schedule above is evaluated in this time zone, including daylight saving transitions.' },
    ],
  },
};

@Component({
  selector: 'app-ehr-vendor-source-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './ehr-vendor-source-form.component.html',
  styleUrl: './ehr-vendor-source-form.component.scss',
})
export class EhrVendorSourceFormComponent implements OnInit, HasUnsavedChanges, SourceConfigFormComponent {
  /** Which EHR vendor this instance is configured for — fixed by whichever thin per-vendor wrapper component
   *  hosts this engine (see e.g. EpicSourceFormComponent), never chosen inside this form itself. Every place that
   *  used to read the form's own vendor `<select>` control now reads this input instead. */
  readonly vendor = input.required<EhrVendor>();

  readonly cancelled = output<void>();
  readonly saved     = output<void>();
  /** The relocated "✕" next to "← Back to library" — closes the whole Node Library dialog outright
   *  (unlike cancel(), which only backs out of this form to the library's sidebar). Mirrors the
   *  original top-level close button's behavior verbatim: immediate, no unsaved-changes prompt. */
  readonly closeAll  = output<void>();
  /** Whether the OUTER Node Library dialog is currently maximized — this form's own topbar renders the
   *  maximize/restore button itself (same relocation as closeAll above) since the outer dialog's own
   *  header row is hidden while this form is showing (see NodeLibraryDialogComponent's .nld-header). */
  readonly isMaximized = input<boolean>(false);
  readonly toggleMaximizeRequest = output<void>();

  /** Set when the backend rejects a save (e.g. "A source connection named 'X' already exists.") — shown
   *  inline under App Name (the field the user actually needs to change to retry) instead of only as a
   *  toast, and clears itself the moment the user edits the name again. The dialog stays open on failure —
   *  `saved` only fires once WizardService.save() actually confirms success (see save() below). */
  protected readonly saveErrorMessage = signal<string | null>(null);

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
  private  readonly unsavedChangesPrompt = inject(UnsavedChangesPromptService);

  /** True only when opened in read-only View mode from the Source Connections page — disables every control and
   *  hides Save. Decided once at open time (see ngOnInit), never toggled live within a single open session. */
  protected get isReadonly(): boolean { return this.wiz.readonlyMode(); }

  // ── Existing Epic Connection picker (canvas-mode create only) ───────────────
  /** No explicit New/Existing toggle — 'new' is the default until a connection is picked (onExistingConnectionSelected
   *  flips it to 'existing'), and the "✕" clear button (clearExistingConnection) flips it back. Drives the
   *  reuse-vs-fork decision in save() and resolvedSourceConnectionId. */
  protected readonly sourceMode = signal<'new' | 'existing'>('new');
  protected readonly existingConnections = signal<SourceConnectionModel[]>([]);
  protected readonly loadingExisting = signal(false);
  protected readonly selectedExistingId = signal<string | null>(null);
  /** Every saved source connection's name (all vendors, not just Epic — getAll() returns everything, this
   *  component just filters existingConnections down to Epic for the dropdown). Populated on ngOnInit for any
   *  canvas-mode create (see loadAllConnectionNames). save() dedupes against it in both branches — cloning an
   *  existing connection verbatim, and leaving a brand-new node on its default/reused App Name — otherwise the
   *  create call collides with "A source connection named '<name>' already exists." only at workflow-build time. */
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
  protected readonly discoveredResourceTypes = signal<string[]>([]);
  protected get resources(): string[] {
    return FHIR_RESOURCES;
  }
  protected isResourceSupported(r: string): boolean { return SUPPORTED_RESOURCE_TYPES.includes(r); }

  /** Of `resources`, only the subset this pipeline actually supports today — drives "Select all" and the
   *  selected-count display in the retrieval-method resource grids so both are scoped to what's selectable
   *  rather than the full (curated, but not necessarily all pipeline-supported) FHIR_RESOURCES list. */
  protected readonly selectableResources = computed(() => this.resources.filter(r => this.isResourceSupported(r)));

  /** Editing an existing source: resource types are locked (identity-defining) — shown prepopulated but disabled. */
  protected get isEditing(): boolean { return this.wiz.isEditing(); }

  protected readonly discStatus   = signal<'idle' | 'loading' | 'done' | 'error'>('idle');
  protected readonly discValues   = signal<FullDiscoveredValues | null>(null);
  // True once discovery actually determined the SMART scope version (vs. leaving the default) — drives the badge.
  protected readonly scopeVersionAuto = signal(false);
  // True once discovery actually determined the Client Auth Method (vs. leaving the default) — drives the badge.
  protected readonly authMethodAuto = signal(false);
  protected readonly testStatus   = signal<'idle' | 'running' | 'ok' | 'fail'>('idle');

  // Backend System only: scopes Epic actually granted the app from a real client_credentials + private_key_jwt
  // exchange run as part of Discover (requested with the fixed wildcard scope 'system/*.*') — distinct from
  // scopesSupported above, which is only what the server advertises, not what this specific app is allowed.
  protected readonly grantedScopesStatus = signal<'idle' | 'loading' | 'done' | 'error'>('idle');
  protected readonly grantedScopes       = signal<string[]>([]);
  protected readonly grantedScopesError  = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    audience:          ['provider-ehr-launch' as EpicAudience, Validators.required],
    environment:       ['sandbox', Validators.required],
    epicBaseUrl:       ['https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4', [Validators.required, urlValidator]],
    tokenEndpoint:     ['', urlValidator],
    authzEndpoint:     ['', urlValidator],
    clientId:          ['', Validators.required],
    // athenahealth only — the practice id required on every FHIR request (ah-practice). Bare number (e.g.
    // "195900"); the backend builds the Organization/a-1.Practice-{id} reference. Conditionally required —
    // see the vendor-keyed effect below, which toggles the validator when `vendor()` is Athenahealth.
    practiceId:        [''],
    // Public + PKCE is the default for interactive apps (EHR launch / standalone / patient) → no client secret needed.
    authMethod:        ['public'],
    clientSecret:      [''],
    // Where the client-credentials grant places client id/secret: 'post' (form body — the default most SMART/
    // FHIR token endpoints accept) or 'basic' (Authorization header). Some client-credentials authorization
    // servers — e.g. Okta-fronted ones, identifiable by a client id like "0oa..." — reject client_secret_post
    // with invalid_client and require Basic instead. Only meaningful for Client Secret auth.
    authPlacement:     ['post' as 'post' | 'basic'],
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
    // No UI picks this anymore (Resource Type & Scopes was removed from the form) — a brand-new source
    // starts with no resource-derived scopes at all. EpicSourceConnectionScopeSyncService (backend) fills
    // this in for real the first time a workflow referencing this source is built, from the union of every
    // connected destination's own selected resource types — see clearInapplicableFields/ngOnInit above.
    // Deliberately not required: an empty selection here is the valid, expected starting state.
    resources:         [[] as string[]],
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
    fullRefreshTimeZone:    [detectBrowserTimeZone()],
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
  // Whole-form bridge (rather than one per control, like the ones above) purely to drive
  // missingRequiredFields()'s recompute — it needs to re-scan on ANY field changing, not just a few.
  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.value });

  /** Live labels of required fields still empty — so the primary button's dimmed state isn't a dead end
   *  the user has to click-and-guess their way through (see focusFirstInvalidField, which this
   *  complements) to find out what's actually blocking Save. Walks this.form's controls directly (NOT
   *  the DOM) so a control still counts even when its section happens to be scrolled out of view or not
   *  yet rendered — focusFirstInvalidField()'s DOM-first approach only works there because it's called
   *  after markAllAsTouched() forces a render pass; this runs continuously as the user types, before
   *  that's ever triggered. The DOM is only consulted afterward, best-effort, for a human label. */
  protected readonly missingRequiredFields = computed<string[]>(() => {
    this.formValue();
    const root = this.formRoot?.nativeElement;

    const labels: string[] = [];
    for (const name of Object.keys(this.form.controls)) {
      const ctrl = this.form.get(name);
      if (!ctrl || ctrl.valid || !ctrl.errors?.['required']) continue;

      const el = root?.querySelector<HTMLElement>(`[formcontrolname="${name}"], [data-control="${name}"]`);
      const label = el?.closest('.eaf-field')?.querySelector('.eaf-label')?.textContent?.trim();
      labels.push((label || name).replace(/\s*\*\s*$/, ''));
    }
    return labels;
  });
  private readonly fullRefreshDayOfMonthValue = toSignal(this.form.controls.fullRefreshDayOfMonth.valueChanges, { initialValue: this.form.controls.fullRefreshDayOfMonth.value });
  private readonly fullRefreshTimeValue       = toSignal(this.form.controls.fullRefreshTime.valueChanges,       { initialValue: this.form.controls.fullRefreshTime.value });
  private readonly fullRefreshTimeZoneValue   = toSignal(this.form.controls.fullRefreshTimeZone.valueChanges,   { initialValue: this.form.controls.fullRefreshTimeZone.value });

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
  protected readonly showPracticeId = computed(() => this.vendor() === 'Athenahealth');

  /** True when this vendor doesn't support the given audience yet (see VENDOR_DISABLED_AUDIENCES) — used to
   *  grey out the option in the audience `<select>`. The strategy is fully implemented server-side; only the
   *  UI hides it until sandbox credentials exist for that audience. */
  protected isAudienceDisabled(audience: EpicAudience): boolean {
    return isAudienceDisabledForVendor(this.vendor(), audience);
  }

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
  /** True once the admin has actually typed into the Private Key / JWKS URL field themselves — distinct from this
   *  component's own auto-fill writes below, which always pass `{ emitEvent: false }` so they never touch this
   *  flag. Guards the keyLoadedFromExistingConnection() auto-fill branch of the effect below: without this, an
   *  admin who deliberately overwrites the hosted URL with their own self-hosted one (see the hint text on this
   *  field) would have it silently reset back to the hosted URL the next time any unrelated signal this effect
   *  reads happens to recompute — which is exactly the "typed a real value, it keeps reverting to localhost" bug.
   *  Reset alongside keyLoadedFromExistingConnection() at both of its call sites, so loading a different node/
   *  connection starts the override tracking fresh. */
  protected readonly jwksUrlUserEdited = signal(false);
  /** True when a real, non-empty JWKS URL was just restored from the backend (SourceAuthenticationConfiguration.
   *  JwksUrl, persisted since docs/backend/13-source-connection-configuration-split-plan.md's JWKS URL discussion)
   *  at either restoreExtendedFieldsFromEditingNode() or populateFormFromSourceConnection(). Guards the same
   *  keyLoadedFromExistingConnection() auto-fill branch as jwksUrlUserEdited() above, for the same reason: without
   *  it, a connection whose key material fields (jwtKid/privateKeyRef/privateKeySecretName) happen to be populated
   *  for a genuinely external/manually-registered key — which looks identical to a Generated/Imported one, since
   *  the backend has no separate provenance marker — would have its real, previously-registered external URL
   *  immediately clobbered by the computed hosted-URL guess as soon as this form loads, before the admin even
   *  gets a chance to type anything. False (falls back to the hosted-URL guess, same as before this field was
   *  persisted) only for a pre-persistence-era row whose JwksUrl column is still null. */
  protected readonly jwksUrlRestoredFromBackend = signal(false);

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
   *  applies: entity mode (WizardService.entityId), canvas mode editing an already-built workflow's node
   *  (WizardService.editingFields()['sourceConnectionId'], embedded there by WorkflowEndpoints on the original
   *  build), or canvas mode's "Existing Source" picker (selectedExistingId — populateFormFromSourceConnection
   *  clones that connection's data into this brand-new node). Null for a brand-new node/connection that hasn't
   *  been saved yet. */
  protected readonly resolvedSourceConnectionId = computed(() =>
    this.wiz.entityId()
    || this.wiz.editingFields()?.['sourceConnectionId']
    || (this.sourceMode() === 'existing' ? this.selectedExistingId() : null)
    || null
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
    const zone = this.fullRefreshTimeZoneValue() || 'UTC';
    switch (this.fullRefreshRecurrenceValue()) {
      case 'weekly': {
        const days = this.fullRefreshDaysOfWeekValue() ?? [];
        const labels = WEEKDAY_OPTIONS.filter(o => days.includes(o.value)).map(o => o.label);
        return labels.length ? `Weekly on ${labels.join(', ')} at ${time} ${zone} — ${cron}` : 'Select at least one day.';
      }
      case 'monthly':
        return `Monthly on day ${this.fullRefreshDayOfMonthValue() || '1'} at ${time} ${zone} — ${cron}`;
      default:
        return `Daily at ${time} ${zone} — ${cron}`;
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

  /** Connection Test is hidden for now — flip this back to re-enable it (see sectionNumbers/template). */
  protected readonly showConnectionTest = false;

  /**
   * Data Retrieval Method / its config are workflow-specific (search criteria, resource types, scopes, pagination,
   * bulk-export settings) — they belong to a workflow's per-node override (see hasExistingChanged's
   * NODE_OVERRIDDEN_FIELDS comment) or, going forward, a per-workflow SourceConfiguration
   * (docs/backend/13-source-connection-configuration-split-plan.md), never to the reusable connection itself.
   * Settings → Source Connections opens this form in 'entity' mode to manage only the connection (URL, auth, JWKS,
   * audience, client details) — so this section is hidden there regardless of what the audience would otherwise
   * show, and wizard.service.ts's entity-mode save() never sends a retrieval payload either.
   */
  // Restricted to Backend System only: Provider Standalone's audienceConfig().showRetrieval also stays true
  // (its 'oneshot' retrievalScope still drives the automated<->oneshot field carry-over/reset logic in
  // onAudienceChange below, keyed off AUDIENCE_FIELD_CONFIG directly), but Standalone no longer gets a visible
  // Data Retrieval Method / Retrieval Configuration section of its own — those are Backend System only now.
  protected readonly showRetrievalSection = computed(() =>
    this.audienceConfig().showRetrieval && this.audience() === 'backend-system' && this.wiz.wizardMode() === 'canvas');

  /** Section numbers shift depending on which optional sections the current audience shows. */
  protected readonly sectionNumbers = computed(() => {
    const cfg = this.audienceConfig();
    let n = 4; // 1 Audience/Env · 2 FHIR Base URL · 3 OAuth Endpoints · 4 Credentials
    const urls              = (this.showApplicationUrlsSection && (cfg.showLaunchUrl || cfg.showRedirect)) ? ++n : null;
    const cds                = cfg.showCdsHooks ? ++n : null;
    const test                = this.showConnectionTest ? ++n : null;
    const retrievalMethod    = this.showRetrievalSection() ? ++n : null;
    const retrievalConfig    = this.showRetrievalSection() ? ++n : null;
    return { urls, cds, test, retrievalMethod, retrievalConfig };
  });

  protected readonly clientIdLabel = computed(() => {
    const env = this.environmentValue();
    if (env === 'production') return 'Client ID — Production';
    if (env === 'non-production') return 'Client ID — Non-Production';
    return 'Client ID — Sandbox';
  });

  protected readonly selectedResources = computed(() => this.resourcesValue() ?? []);

  /**
   * Backend System (and any other audience with showResourcePicker: false) normally derives its scopes from the
   * Data Retrieval Method's own Resource Type control — but that whole section is hidden in entity mode
   * (showRetrievalSection), so activeRetrievalResourceTypes() is always empty there. Entity mode reads from the
   * `resources` control instead, regardless of audience — WizardService.openEntity() seeds it from the existing
   * connection's dto.retrieval.resourceTypes when editing (empty for a brand-new one, same as canvas mode; see
   * ngOnInit's remarks on why nothing is preselected by default anymore).
   */
  protected readonly showResourcePickerSection = computed(() =>
    this.audienceConfig().showResourcePicker || this.wiz.wizardMode() === 'entity');

  protected readonly scopeString = computed(() => {
    const aud = this.audience();
    const cfg = this.audienceConfig();
    // Backend System has no shared Resource Type picker — its scopes are derived from
    // whichever Resource Type list is set on the currently selected retrieval method (canvas mode only —
    // see showResourcePickerSection for why entity mode always uses selectedResources() instead).
    const res = this.showResourcePickerSection() ? this.selectedResources() : this.activeRetrievalResourceTypes();
    const fixed = cfg.includeInteractiveScopes
      ? ['openid', 'fhirUser', 'offline_access', aud === 'provider-ehr-launch' ? 'launch' : 'launch/patient']
      : [];
    // v2 = granular per-resource read+search (SMART v2 uses .rs); v1 = coarse per-resource .read.
    const suffix = this.scopeVersionValue() === 'v2' ? 'rs' : 'read';
    return [...fixed, ...res.map(r => `${cfg.scopePrefix}/${r}.${suffix}`)].join('\n');
  });


  constructor() {
    // WizardService.ehrType is the one place downstream code (save()'s SourceConnectionRequest.sourceSystemType,
    // the destination wizard's [sourceVendor] binding, etc.) reads the vendor from — it used to be driven by this
    // form's own (now-removed) EHR `<select>`. With vendor fixed per hosting wrapper component instead, keep it in
    // sync here so every existing downstream reader keeps working unchanged.
    //
    // Skipped for an entity-mode edit/view of an ALREADY-persisted connection (wiz.entityId() set): openEntity()
    // already seeded wiz.ehrType from that connection's real, saved sourceSystemType, which can legitimately
    // differ from this instance's fixed `vendor` input — SourceConnectionListComponent falls back to rendering
    // this Epic-shaped engine for a handful of vendors (GenericFhir/Hl7v2/legacy NewEHR placeholders) that don't
    // have — or don't yet have — their own dedicated entity-mode-capable form (see its EHR_VENDOR_TO_SOURCE_FORM_KEY
    // fallback). Overwriting ehrType there would silently change that connection's vendor to 'Epic' on next save.
    // Canvas mode (and entity-mode CREATE, where entityId() is still null) always syncs: there's no pre-existing
    // saved vendor to protect, and the point IS to make the new node/connection's vendor match this instance's.
    effect(() => {
      const vendor = this.vendor();
      if (this.wiz.wizardMode() === 'canvas' || !this.wiz.entityId()) {
        this.wiz.ehrType.set(vendor);
      }
    });

    // A saved connection's audience can predate a vendor's disabled-audience list (e.g. was created before
    // Provider Standalone/EHR Launch were hidden for Athenahealth), or this instance's `vendor` could change
    // after the form already restored one. Fall back to Backend System — always enabled for every vendor —
    // rather than leaving the form silently on an audience its own <select> now shows as disabled.
    effect(() => {
      const vendor = this.vendor();
      const current = this.audience();
      if (isAudienceDisabledForVendor(vendor, current)) {
        this.form.controls.audience.setValue('backend-system');
      }
    });

    // Practice ID is required for athenahealth (every FHIR request needs ah-practice) and inapplicable to every
    // other vendor — toggle the validator here rather than in the template so Save's own validity check
    // (missingRequiredFields/form.valid) agrees with what showPracticeId() renders.
    effect(() => {
      const control = this.form.controls.practiceId;
      control.setValidators(this.showPracticeId() ? [Validators.required] : []);
      control.updateValueAndValidity({ emitEvent: false });
    });

    // SMART Scope Version has no UI (hidden — see the template's remarks) and defaults to 'v2' (granular
    // system/{Type}.rs), which is correct for Epic. athenahealth's Backend System app registrations verified
    // against the live preview sandbox are provisioned with v1 coarse scopes only (system/{Type}.read) — sending
    // v2 scopes gets rejected by the token endpoint with "Invalid Scope: One or more scopes are not configured
    // for the authorization server resource." detectScopeVersion (real evidence from a successful Discover call)
    // still wins if it ever fires — this is only a default for when it hasn't.
    effect(() => {
      if (this.vendor() === 'Athenahealth' && !this.scopeVersionAuto()) {
        this.form.controls.scopeVersion.setValue('v1');
      }
    });

    // Keeps the "Private Key / JWKS URL" field itself correct for a Generated/Imported key, instead of only
    // showing the real URL in a toast — once resolvedSourceConnectionId() is known (after the first save, or
    // immediately when editing an already-saved connection), this is the one real, always-correct value; nothing
    // typed for a manual/external key is ever touched.
    //
    // Also fires whenever keyLoadedFromExistingConnection() is true (cloning "Existing Source" in the canvas
    // wizard, or editing a node/entity whose key was already configured) — the backend has no field recording
    // whether that key was originally Generated/Imported/Manual (see docs/backend/13-source-connection-
    // configuration-split-plan.md's JWKS URL discussion), so keySource defaults back to 'manual' on every clone
    // and isGenKey()/isImportKey() alone would never fire here, leaving `jwksUrl` — a required field whenever
    // auth method is JWT — blank and silently blocking save (the exact "click Update, nothing happens" bug
    // already fixed once for `resources`). Auto-filling FHIRBridge's hosted URL is exactly correct when the
    // original key really was Generated/Imported, and a visible, editable placeholder to overwrite (matching the
    // existing hint text below the field) when it wasn't — strictly better than a blank field that blocks
    // submission with no visible explanation either way.
    //
    // The keyLoadedFromExistingConnection() branch only applies while jwksUrlUserEdited() is still false: once the
    // admin has actually typed their own (e.g. self-hosted) URL into this field, this effect must stop reasserting
    // the hosted one — otherwise any later recompute of this effect's other signal reads (isGenKey()/isImportKey())
    // silently reverts a real, deliberate edit back to FHIRBridge's own localhost/hosted URL. It also only applies
    // while jwksUrlRestoredFromBackend() is false: JwksUrl is now persisted server-side (see that signal's own
    // remarks), so a connection that already has a real saved URL — hosted or external — must show that back
    // exactly as saved, not have it overwritten by a guess just because key-reference fields also happen to be
    // present (which is also true of a manually-registered external key). isGenKey()/isImportKey() stay
    // unconditional: a key just Generated/Imported *this session* is always genuinely hosted at that URL, so
    // there's nothing legitimate to type over it.
    effect(() => {
      const url = this.liveJwksUrl();
      if (!url) return;
      if (this.isGenKey() || this.isImportKey()) {
        this.form.controls.jwksUrl.setValue(url, { emitEvent: false });
      } else if (this.keyLoadedFromExistingConnection() && !this.jwksUrlRestoredFromBackend() && !this.jwksUrlUserEdited()) {
        this.form.controls.jwksUrl.setValue(url, { emitEvent: false });
      }
    });

    // Only genuine user keystrokes reach here — every programmatic write above passes `{ emitEvent: false }`.
    this.form.controls.jwksUrl.valueChanges.subscribe(() => this.jwksUrlUserEdited.set(true));
  }

  ngOnInit(): void {
    // "New Source" (the default picker state) needs the same collision defense as "Existing Source" cloning —
    // otherwise a brand-new node left on the default App Name silently collides with a prior connection of that
    // exact name, and the raw backend "already exists" error only surfaces later, at workflow build time.
    if (this.showSourcePicker()) {
      this.loadAllConnectionNames();
      this.loadExistingConnections();
    }

    if (this.wiz.isEditing()) {
      // Pre-populate all fields from saved node data
      this.form.controls.audience.setValue(this.wiz.epicAudience() as EpicAudience);
      this.form.controls.environment.setValue(this.wiz.env());
      this.form.controls.clientId.setValue(this.wiz.clientId());
      this.form.controls.practiceId.setValue(this.wiz.practiceId());
      this.form.controls.authMethod.setValue(this.wiz.authMethod());
      this.form.controls.authPlacement.setValue(this.wiz.authPlacement());
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
    this.prevAuthMethod = this.form.controls.authMethod.value as 'public' | 'secret' | 'jwt';
    this.prevRetrievalMethod = this.form.controls.retrievalMethod.value;

    // A brand-new connection starts with no resource-derived scopes at all — no UI picks this anymore (the
    // Resource Type & Scopes picker was removed). EpicSourceConnectionScopeSyncService (backend) fills this
    // in for real the first time a workflow referencing this source is built, from the union of every
    // connected destination's own selected resource types. Editing an existing connection keeps whatever was
    // actually saved (restored above), never overwritten here.
    this.ensureRetrievalResourceTypeDefault();

    this.lockRetrievalMethodIfOneShot();
    this.syncValidators();

    this.form.controls.audience.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((next) => {
        const nextAudience = next as EpicAudience;
        this.clearInapplicableFields(this.prevAudience, nextAudience);
        this.prevAudience = nextAudience;
        this.lockRetrievalMethodIfOneShot();
        // Client Auth Method follows the audience — Backend System is JWT-only per SMART Backend Services;
        // every interactive audience is Public Client + PKCE. setValue (not patchValue) so this always goes
        // through the authMethod.valueChanges subscription below and clears whatever the previous method's
        // fields were, exactly as if the user had picked the new method themselves.
        this.form.controls.authMethod.setValue(defaultAuthMethodFor(this.vendor(), nextAudience));
        this.syncValidators();
      });

    this.form.controls.authMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((next) => {
        const nextMethod = next as 'public' | 'secret' | 'jwt';
        this.clearInapplicableAuthFields(this.prevAuthMethod, nextMethod);
        this.prevAuthMethod = nextMethod;
        this.syncValidators();
      });

    // Switching the base URL to/from a loopback address flips whether the OAuth/credential fields are required.
    this.form.controls.epicBaseUrl.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncValidators());

    // Generate/Import leave jwksUrl blank until save (see syncValidators()'s jwksUrlServerManaged) — re-sync the
    // moment the admin picks either, so the required validator drops immediately instead of only on the next
    // unrelated field change.
    this.form.controls.keySource.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncValidators());

    // Clear a previous save failure (e.g. "name already exists") the moment the user edits the name
    // again — it was already surfaced and shouldn't linger once they've acted on it.
    this.form.controls.appName.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.saveErrorMessage()) {
          this.saveErrorMessage.set(null);
          this.form.controls.appName.setErrors(null);
        }
      });

    this.form.controls.retrievalMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((next) => {
        const nextMethod = (next as RetrievalMethod) || '';
        this.clearInapplicableRetrievalFields(this.prevRetrievalMethod, nextMethod);
        this.prevRetrievalMethod = nextMethod;
        this.ensureRetrievalResourceTypeDefault();
        this.syncRetrievalValidators();
      });

    this.form.controls.exportScope.valueChanges
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
    this.jwksUrlUserEdited.set(false);
    // A real JWKS URL is now persisted (SourceAuthenticationConfiguration.JwksUrl — see fieldsFromEntityDto() and
    // the canvas node's own 'JWKS URL' key) and was just restored above via setIfPresent — don't let the auto-fill
    // effect below stomp a real, previously-registered external URL just because key-reference fields also happen
    // to be present. Only a pre-persistence-era row (migrated with a null JwksUrl) falls through to the computed
    // hosted-URL guess.
    this.jwksUrlRestoredFromBackend.set(!!fields['JWKS URL']);

    // Same reasoning as jwksUrl above: whatever Epic granted on this node's last successful Discover was written
    // to 'Discovered scopes' by save() — restore it so reopening the node shows the same badge instead of looking
    // like Discover was never run.
    if (fields['Discovered scopes']) {
      this.grantedScopes.set(fields['Discovered scopes'].split(/\s+/).filter(Boolean));
      this.grantedScopesStatus.set('done');
    }

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
    setIfPresent('fullRefreshTimeZone', 'Full refresh time zone');

    setIfPresent('pageSize', 'Page size (_count)');
    setIfPresent('sortOrder', 'Sort (_sort)');
    setIfPresent('includeLinked', 'Include (_include)');
    setIfPresent('revIncludeLinked', 'Reverse include (_revinclude)');
    setIfPresent('retryPolicy', 'Retry policy');
    setIfPresent('timeoutSeconds', 'Timeout (seconds)');
    setIfPresent('maxRecordsPerRun', 'Max records per run');

    this.ensureRetrievalResourceTypeDefault();
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
    if (auth?.jwksUrl) fields['JWKS URL'] = auth.jwksUrl;
    if (auth?.discoveredScopes?.length) fields['Discovered scopes'] = auth.discoveredScopes.join(' ');
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
  private prevAuthMethod: 'public' | 'secret' | 'jwt' = 'public';
  private prevRetrievalMethod: RetrievalMethod | '' = '';

  /** Clears values for fields that are no longer applicable after an audience switch. */
  private clearInapplicableFields(prev: EpicAudience, next: EpicAudience): void {
    const prevCfg = AUDIENCE_FIELD_CONFIG[prev];
    const nextCfg = AUDIENCE_FIELD_CONFIG[next];

    if (prevCfg.showRedirect && !nextCfg.showRedirect) {
      this.form.patchValue({ callbackUrl: '' });
    }
    // Entering the other way: callbackUrl was blanked out above the last time an audience that hides it was
    // active — restore the same sensible default ngOnInit itself starts every new form with, so a real, still-
    // required field doesn't stay permanently empty (and Add to Pipeline permanently blocked) just because the
    // user passed through Backend System (or any other showRedirect: false audience) on the way here. Guarded on
    // "currently empty" so a genuinely restored/edited value is never clobbered.
    if (!prevCfg.showRedirect && nextCfg.showRedirect && !this.form.controls.callbackUrl.value) {
      this.form.patchValue({ callbackUrl: OAUTH_DEFAULT_URLS.redirectUri });
    }

    if (prevCfg.showLaunchUrl && !nextCfg.showLaunchUrl) {
      this.form.patchValue({ launchUrl: '' });
    }
    // Same restore-on-entry reasoning as callbackUrl above.
    if (!prevCfg.showLaunchUrl && nextCfg.showLaunchUrl && !this.form.controls.launchUrl.value) {
      this.form.patchValue({ launchUrl: OAUTH_DEFAULT_URLS.launchUrl });
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

    // Entity mode always needs a populated `resources` control regardless of audience (see
    // showResourcePickerSection) — switching audiences there must never clear it to [], or the control is left
    // required-and-empty with no picker UI to refill it (exactly the "Create Source Connection does nothing"
    // bug: form.invalid silently blocks save() with no visible field to fix, since the shared picker section
    // doesn't render for audiences with showResourcePicker: false).
    const prevShowsResources = prevCfg.showResourcePicker || this.wiz.wizardMode() === 'entity';
    const nextShowsResources = nextCfg.showResourcePicker || this.wiz.wizardMode() === 'entity';

    if (prevShowsResources && !nextShowsResources) {
      this.form.patchValue({ resources: [] });
    }
    // Switching the other way: default to every MVP1-supported resource type's scope, same as the initial
    // load — there's no visible picker for the user to fill this in themselves anymore.
    if (!prevShowsResources && nextShowsResources) {
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
        fullRefreshTimeZone: detectBrowserTimeZone(),
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
        fullRefreshTimeZone: detectBrowserTimeZone(),
        pageSize: '100', sortOrder: '', revIncludeLinked: '',
        retryPolicy: 'exponential', timeoutSeconds: '30',
      });
    }
  }

  /** Clears whichever Client Auth Method fields belonged only to the method being left, whenever the value
   *  actually changes — otherwise a Client Secret or JWKS/private-key value typed under one method silently
   *  survives switching to another and reappears if the user switches back, riding along into save() unnoticed. */
  private clearInapplicableAuthFields(prev: 'public' | 'secret' | 'jwt', next: 'public' | 'secret' | 'jwt'): void {
    if (prev === next) return;
    const patch: Record<string, unknown> = {};
    if (prev === 'secret') {
      patch['clientSecret'] = '';
    }
    if (prev === 'jwt') {
      patch['jwksUrl'] = '';
      patch['jwtKid'] = '';
      patch['privateKeyRef'] = '';
      patch['privateKeySecretName'] = '';
      patch['keySource'] = 'manual';
    }
    if (Object.keys(patch).length) this.form.patchValue(patch);
  }

  /** Clears whichever Retrieval Method fields belonged only to the method being left, whenever the value actually
   *  changes — mirrors clearInapplicableAuthFields for the Data Retrieval Method dropdown (Backend System only),
   *  reading field ownership straight from RETRIEVAL_METHOD_CONFIG so it never drifts from the field list/validator
   *  logic that already reads the same registry. */
  private clearInapplicableRetrievalFields(prev: RetrievalMethod | '', next: RetrievalMethod | ''): void {
    if (prev === next) return;
    const prevKeys = prev ? RETRIEVAL_METHOD_CONFIG[prev].fields.map(f => f.key) : [];
    const nextKeys = new Set(next ? RETRIEVAL_METHOD_CONFIG[next].fields.map(f => f.key) : []);
    const patch: Record<string, unknown> = {};
    for (const key of prevKeys) {
      if (!nextKeys.has(key)) patch[key] = RETRIEVAL_FIELD_DEFAULTS[key];
    }
    if (!Object.keys(patch).length) return;
    // runMode's own valueChanges subscription disables Incremental Cursor while Run Mode is Incremental Sync —
    // re-enable it here too whenever runMode itself is being cleared out, mirroring clearInapplicableFields'
    // existing "leaving showRetrieval" handling, so the control is never left stuck disabled after the field
    // that disabled it has just been reset out from under it.
    if ('runMode' in patch) this.form.controls.incrementalCursor.enable({ emitEvent: false });
    this.form.patchValue(patch);
  }

  /** Every retrieval method's own Resource Type control is hidden entirely from the UI (see RETRIEVAL_METHOD_CONFIG
   *  fields) — the destination node's own "dest_resources" picker (and, at execution time, the backend's
   *  destination-derived fallback) already captures the same choice. Scope generation
   *  (activeRetrievalResourceTypes/scopeString) still reads whichever control belongs to the active method, so a
   *  connection needs a non-empty value from somewhere other than a picker the admin can no longer see. Defaults it
   *  to every MVP1 resource type, same as the shared Resource Type picker already does for audiences that show it
   *  (see the showResourcePicker default in ngOnInit). Never overwrites a real, already-populated value — a
   *  genuinely restored/edited selection (from a saved connection or an edited canvas node) is left exactly as-is. */
  private ensureRetrievalResourceTypeDefault(): void {
    const key = ({
      subscription: 'subscriptionResourceType',
      webhook: 'webhookResourceType',
      'search-rest': 'searchRestResourceType',
      'bulk-export': 'bulkExportResourceType',
    } as const)[this.form.controls.retrievalMethod.value as RetrievalMethod];
    if (!key) return;
    const control = this.form.controls[key];
    if (control.value.length > 0) return;
    control.setValue([...FHIR_RESOURCES]);
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
    // Not required: there's no UI left to hand-pick this (the Resource Type & Scopes picker was removed —
    // see ngOnInit's remarks), and the backend never needs it upfront either — Epic's base OAuth scopes are
    // included regardless of resource count, and EpicSourceConnectionScopeSyncService fills in the real,
    // usage-derived scopes later for any interactive source (EHR-launch/Standalone/Patient) once a workflow
    // wires it to a destination. Leaving this required with no control to satisfy it made the form
    // permanently invalid for every showResourcePicker audience.
    apply('resources',     false);
    apply('clientSecret',  method === 'secret');
    // Generate/Import (Backend System only) leave this blank on purpose — the real JWKS URL is this connection's
    // own .well-known/jwks.json, only known once it has an id after save (see the readonly condition on this field
    // in the template and generateKeyPair()/importPrivateKey() above, neither of which populate it).
    const jwksUrlServerManaged = this.audience() === 'backend-system' && (this.isGenKey() || this.isImportKey());
    apply('jwksUrl',       method === 'jwt' && !jwksUrlServerManaged, true);
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
    // Gated by showRetrievalSection (audience config AND wizardMode==='canvas'), not audience config alone — a
    // required-but-hidden retrieval field in entity mode would otherwise leave the form permanently invalid,
    // since Settings → Source Connections never shows this section for the user to fill it in.
    const showSection = this.showRetrievalSection();
    const methodCfg = showSection ? this.retrievalConfig() : null;
    const visible   = methodCfg ? this.visibleRetrievalFields() : [];
    const visibleKeys = new Set(visible.map(f => f.key));

    const apply = (name: string, required: boolean): void => {
      const ctrl = this.form.get(name)!;
      ctrl.setValidators(required ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    };

    apply('retrievalMethod', showSection);

    for (const key of RETRIEVAL_FIELD_KEYS) {
      const field = methodCfg?.fields.find(f => f.key === key);
      let required = !!field && visibleKeys.has(key) && field.required;
      if (required && field?.requiredUnless && this.form.get(field.requiredUnless.key)?.value === field.requiredUnless.value) {
        required = false;
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

  // ── Existing Epic Connection picker (canvas-mode create only) ───────────────
  /** Loaded unconditionally as soon as the picker is offered (see ngOnInit) — the dropdown has no separate
   *  "switch to Existing" step to trigger it from, it's just always there. */
  private loadExistingConnections(): void {
    if (this.existingConnections().length > 0 || this.loadingExisting()) return;
    this.loadingExisting.set(true);
    this.sourceConnectionSvc.getAll().subscribe({
      next: connections => {
        this._allConnectionNames = new Set(connections.map(c => c.name));
        this.existingConnections.set(connections.filter(c => c.sourceSystemType === this.vendor()));
        this.loadingExisting.set(false);
      },
      error: () => {
        this.loadingExisting.set(false);
        this.toast.show('Failed to load', `Could not load existing ${this.vendor()} source connections.`, 'error');
      },
    });
  }

  /** The "✕" next to the dropdown — undoes a clone and returns the form to a blank "New Source" state. This is
   *  the only way back to blank now that there's no explicit New/Existing toggle to switch away from. */
  protected clearExistingConnection(): void {
    this.sourceMode.set('new');
    this.resetToBlankNewSource();
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

  /** Clearing the picker after a clone must undo it — otherwise the form silently keeps whatever the
   *  last-selected existing connection populated, even though the dropdown now shows no selection. */
  private resetToBlankNewSource(): void {
    this.selectedExistingId.set(null);
    this._existingBaseline = null;
    this._awaitingBaselineSnapshot = false;
    this.form.reset();
    this.discoveredResourceTypes.set([]);
    this.discStatus.set('idle');
    this.discValues.set(null);
    this.scopeVersionAuto.set(false);
    this.authMethodAuto.set(false);
    this.testStatus.set('idle');
    this.wiz.trustedIssuers.set('');

    this.prevAudience = this.audience();
    this.prevAuthMethod = this.form.controls.authMethod.value as 'public' | 'secret' | 'jwt';
    this.prevRetrievalMethod = this.form.controls.retrievalMethod.value;
    this.lockRetrievalMethodIfOneShot();
    this.syncValidators();
    this.syncRetrievalValidators();
  }

  protected onExistingConnectionSelected(id: string): void {
    this.sourceMode.set('existing');
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
          '.well-known/jwks.json once you save — register that URL with the EHR.'
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
          '.well-known/jwks.json once you save — register that URL with the EHR.'
        );
      },
      error: (err) => {
        this.keyGenStatus.set('idle');
        const message = err?.error?.message ?? err?.error?.title ?? 'Failed to import the private key.';
        this.toast.show('Import failed', message, 'error');
      },
    });
  }

  /** Just the connection name — base URL/audience/client-id details are shown once selected, not in the picker. */
  protected existingConnectionLabel(conn: SourceConnectionModel): string {
    return conn.name;
  }

  /**
   * Clones a persisted SourceConnection's data into this (still-unsaved) canvas node's form — lets a new
   * pipeline node start from an already-configured connection instead of re-entering everything by hand.
   * Mirrors WizardService.openEntity()'s field mapping, but patches the reactive form directly: this is canvas
   * mode, so there's no backend entity id to attach to — the cloned values just become this new node's own,
   * independently editable, canvas fields once Save is clicked.
   */
  private populateFormFromSourceConnection(dto: SourceConnectionModel): void {
    // Clear whatever was left in the form from a prior in-progress "New Source" entry (or a previously selected
    // "Existing Source") before patching in this connection's data — otherwise any field not explicitly named in
    // the patchValue below (CDS Hooks, a different retrieval method's fields, etc.) silently carries over instead
    // of starting blank. The patchValue immediately below repopulates every field this DTO actually has data for;
    // reset() only affects the ones it doesn't, which should start blank rather than keep stale prior input.
    this.form.reset();

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
    // A connection created via Settings (entity mode) always has retrieval: null — narrowing Settings to
    // connection-only fields (docs/backend/13-source-connection-configuration-split-plan.md) means retrieval
    // config genuinely never existed there; it isn't a gap in what got restored. 'search-rest' is the closest
    // thing to a sane default retrieval method (used by the large majority of real Backend System/Standalone
    // sources), and resolving it HERE — not leaving retrievalMethod blank — is what lets the baseline below
    // capture a complete, valid configuration that requires no further edit to satisfy validation.
    const resolvedRetrievalMethod = (retrieval?.retrievalMethod as RetrievalMethod) || 'search-rest';
    const retrievalResourceKey = retrievalResourceKeyByMethod[resolvedRetrievalMethod];

    this.form.patchValue({
      audience,
      environment: 'sandbox',
      appName: dto.name,
      epicBaseUrl: dto.baseUrl,
      tokenEndpoint: dto.authentication?.tokenEndpoint ?? '',
      clientId: dto.authentication?.clientId ?? '',
      practiceId: dto.authentication?.practiceId ?? '',
      authMethod,
      authPlacement: (dto.authentication?.authPlacement as 'post' | 'basic') || 'post',
      jwtKid: dto.authentication?.keyId ?? '',
      privateKeyRef: dto.authentication?.privateKeyKeyVaultName ?? '',
      privateKeySecretName: dto.authentication?.privateKeySecretName ?? '',
      jwksUrl: dto.authentication?.jwksUrl ?? '',
      // Required for EHR-Launch/Standalone audiences, but neither is guaranteed to be persisted on the source
      // connection being cloned (e.g. LaunchUrl is NULL on plenty of real rows, and EHR-Launch connections never
      // persist a resource-type selection server-side at all) — fall back to whatever the form already holds
      // (ngOnInit's own sensible defaults) rather than blanking a required field out and silently failing Save.
      launchUrl: dto.interactive?.launchUrl || this.form.controls.launchUrl.value,
      callbackUrl: dto.interactive?.redirectUris?.[0] ?? this.form.controls.callbackUrl.value,
      // Unlike launchUrl/callbackUrl above (whose FormBuilder-literal initial value is already a real, usable
      // default), `resources`' own literal initial is `[]` — it only becomes FHIR_RESOURCES via an explicit
      // ngOnInit-time setValue for new/non-editing sources. Since form.reset() (just above, at the top of this
      // method) wipes that back to `[]`, falling back to `this.form.controls.resources.value` here would silently
      // leave a required field required-and-empty on every clone whose DTO has no persisted resourceTypes (e.g.
      // Provider Standalone connections, which never persist a resource-type selection server-side) — exactly the
      // "Add to Pipeline stays disabled" bug this fallback exists to prevent. FHIR_RESOURCES directly is the same
      // fallback already used a few lines below for the per-method retrieval resource-type control.
      resources: retrieval?.resourceTypes?.length ? [...retrieval.resourceTypes] : [...FHIR_RESOURCES],
      retrievalMethod: resolvedRetrievalMethod,
      searchCriteria: retrieval?.searchCriteria ?? '',
      incrementalCursor: retrieval?.incrementalSyncEnabled ?? false,
      // "Run Mode" (incremental/full/manual) has no backend persistence at all — only the derived
      // incrementalSyncEnabled boolean is stored on the connection's Retrieval config, so 'full' vs 'manual' can
      // never be told apart from a saved connection (and there's nothing at all to derive from when retrieval is
      // null, e.g. a Settings-created connection). Leaving this blank (the previous behavior) left a required
      // field required-and-empty on every clone of a search-rest source: the user was forced to pick SOMETHING to
      // get past validation, and picking anything at all — even the value the original effectively had — flipped
      // hasExistingChanged() to true purely because the untouched baseline was '' instead of a real value, forking
      // a brand-new, independent connection on every single clone (exactly the "created BS Gaurav, cloned it, got
      // BS Gaurav-1 anyway" bug this field's absence caused). 'manual' is the only safe reconstruction when
      // incrementalSyncEnabled isn't explicitly true — it's the one Run Mode value that requires no further fields
      // (Schedule/Poll Frequency and the Full Refresh calendar block only appear for 'incremental'/'full'), so
      // restoring it never forces an edit the way defaulting to 'full' would.
      runMode: retrieval?.incrementalSyncEnabled ? 'incremental' : 'manual',
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

    // Same reasoning as resolvedRetrievalMethod above: this per-method Resource Type control needs a non-empty
    // value even when the cloned connection has no retrieval data to restore it from — same full-MVP1-set
    // fallback the shared `resources` picker's own default above uses (see also ensureRetrievalResourceTypeDefault,
    // for the case where the field is one of the retrieval methods' own hidden controls and this clone had no
    // retrieval data at all).
    this.form.get(retrievalResourceKey)?.setValue(
      retrieval?.resourceTypes?.length ? [...retrieval.resourceTypes] : [...FHIR_RESOURCES]
    );

    // Cloned key material still deserves the "already configured, confirm before replacing" guard — clicking
    // Generate/Import here would fork a brand-new SourceConnection on save (this is a clone into a new node, not
    // an edit-in-place), but the point of the guard is preventing an accidental click from discarding key info
    // that took effort to have populated, which applies just as much to cloned data as to the original entity's.
    this.keyLoadedFromExistingConnection.set(
      !!(dto.authentication?.keyId && dto.authentication?.privateKeyKeyVaultName && dto.authentication?.privateKeySecretName)
    );
    this.jwksUrlUserEdited.set(false);
    // Same reasoning as restoreExtendedFieldsFromEditingNode() — a persisted, real JwksUrl just got patched in
    // above; don't let the auto-fill effect override it with the computed hosted-URL guess.
    this.jwksUrlRestoredFromBackend.set(!!dto.authentication?.jwksUrl);

    this.wiz.trustedIssuers.set(dto.interactive?.trustedIssuers?.join(', ') ?? '');
    this.discoveredResourceTypes.set(retrieval?.resourceTypes?.length ? [...retrieval.resourceTypes] : []);

    // Restore whatever Epic granted on this connection's last successful Discover, so reopening it shows the
    // same badge instead of looking like Discover was never run — re-running Discover overwrites this as usual.
    if (dto.authentication?.discoveredScopes?.length) {
      this.grantedScopes.set([...dto.authentication.discoveredScopes]);
      this.grantedScopesStatus.set('done');
    } else {
      this.grantedScopes.set([]);
      this.grantedScopesStatus.set('idle');
    }

    this.prevAudience = audience;
    this.prevAuthMethod = authMethod;
    this.prevRetrievalMethod = resolvedRetrievalMethod;
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

  /**
   * The ONLY fields that count toward the fork-vs-reuse decision below — an allowlist, not a denylist. This is
   * the direct implementation of "Source Connection should only include connection details (URL, auth, JWKS,
   * audience, client details); search criteria, advanced options, scopes, and any other workflow-specific
   * configuration should never force a new connection" (docs/backend/13-source-connection-configuration-split-
   * plan.md) — picking "Existing Source" and configuring a workflow around it must never fork, no matter what
   * gets configured, unless one of THESE specific fields (the real, persisted identity of the connection) changes.
   * An earlier denylist approach (excluding known workflow-specific fields one at a time) kept missing fields as
   * they were discovered in practice — e.g. `runMode`/`incrementalCursor` still forked a real connection
   * ("BS Gaurav" → "BS Gaurav-2") even after `resources`/`searchCriteria` were excluded. An allowlist is immune to
   * that failure mode: anything not listed here (all of RETRIEVAL_FIELD_KEYS, `resources`, CDS Hooks, `jwksUrl`,
   * `keySource`, `scopeVersion`, …) is workflow-specific by default and can never trigger a fork, matching every
   * field this form actually sends as part of CreateSourceConnectionRequest (name, base URL, auth, interactive
   * launch settings) — nothing else is ever persisted onto the SourceConnection entity itself.
   */
  private static readonly CONNECTION_IDENTITY_FIELDS = new Set<string>([
    'appName', 'audience', 'environment', 'epicBaseUrl',
    'tokenEndpoint', 'authzEndpoint', 'clientId', 'authMethod',
    'jwtKid', 'privateKeyRef', 'privateKeySecretName',
    'launchUrl', 'launchDisplayMode', 'callbackUrl',
  ]);

  /** True once the user has edited a connection-identity field (see CONNECTION_IDENTITY_FIELDS) away from what
   *  populateFormFromSourceConnection() cloned in (as re-confirmed by discovery) — the save-time signal for
   *  "fork a new connection" vs "reuse this one untouched" (see save()). `clientSecret` is never in the allowlist
   *  since it's never populated by the clone in the first place (secrets never come back from the API). */
  protected hasExistingChanged(): boolean {
    if (!this._existingBaseline) return false;
    const pick = (v: Record<string, unknown>) =>
      Object.fromEntries(Object.entries(v).filter(([key]) =>
        EhrVendorSourceFormComponent.CONNECTION_IDENTITY_FIELDS.has(key)));
    return JSON.stringify(pick(this.form.getRawValue())) !== JSON.stringify(pick(this._existingBaseline));
  }

  protected runDiscover(): void {
    const url = this.form.controls.epicBaseUrl.value.trim();
    if (!url) { this.toast.show('URL required', 'Enter the FHIR Base URL first.'); return; }
    const envKey: EnvKey = this.form.controls.environment.value === 'production' ? 'production' : 'sandbox';
    this.discStatus.set('loading');
    this.grantedScopesStatus.set('idle');
    this.grantedScopes.set([]);
    this.grantedScopesError.set(null);

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
        const detectedAuthMethod = detectAuthMethod(this.vendor(), this.audience(), result.tokenEndpointAuthMethods);
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
        if (this.audience() === 'backend-system' && !result.smartConfigurationError) {
          this.runBackendAuthScopeProbe(dv.tokenEndpoint);
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

  /** Backend System only: fired from runDiscover() once the token endpoint resolves — exchanges the fixed
   *  wildcard scope 'system/*.*' via client_credentials + private_key_jwt using whatever clientId/signing-key
   *  fields are already on the form, and shows the scopes Epic actually granted. Silently skipped when the
   *  signing key hasn't been configured yet (nothing to sign with) rather than surfacing an error. */
  private runBackendAuthScopeProbe(tokenEndpoint: string): void {
    const clientId = this.form.controls.clientId.value.trim();
    const keyId = this.form.controls.jwtKid.value.trim();
    const privateKeyVaultName = this.form.controls.privateKeyRef.value.trim();
    const privateKeySecretName = this.form.controls.privateKeySecretName.value.trim();
    if (!clientId || !tokenEndpoint || !privateKeyVaultName || !privateKeySecretName) {
      return;
    }

    this.grantedScopesStatus.set('loading');
    // Epic's system scope grammar has no literal wildcard access-level ('system/*.*' is invalid and gets rejected
    // as invalid_scope) — the access level must be a real suffix: '.read' for v1 (coarse), '.rs' for v2 (granular),
    // matching whichever version scopeString() above already uses for this connection.
    const suffix = this.scopeVersionValue() === 'v2' ? 'rs' : 'read';
    this.discovery.testBackendAuthScopes({
      tokenEndpoint, clientId, keyId: keyId || null, privateKeyVaultName, privateKeySecretName,
      scope: `system/*.${suffix}`,
    }).subscribe({
      next: (result) => {
        if (result.success) {
          this.grantedScopes.set(result.grantedScopes);
          this.grantedScopesStatus.set('done');
        } else {
          this.grantedScopesError.set(result.error ?? 'Epic did not grant any scopes.');
          this.grantedScopesStatus.set('error');
        }
      },
      error: (err) => {
        const msg = typeof err?.error?.error === 'string' ? err.error.error : 'Could not authenticate with Epic.';
        this.grantedScopesError.set(msg);
        this.grantedScopesStatus.set('error');
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
    if (!baseUrl) { this.toast.show('Base URL required', 'Enter the FHIR base URL first.'); return; }
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

  /**
   * The flat `fields` bag this form's current (assumed-valid) values map to — everything save() writes onto the
   * node/entity EXCEPT the "reuse vs fork an existing connection" bookkeeping (`sourceConnectionId`/
   * `sourceConnectionResolved`), which only applies to save()'s own canvas-mode "Existing Source" flow, not to a
   * generic caller reading this form's state via getFields(). Shared by save() and getFields() so the two can
   * never drift apart on what a given set of form values actually produces.
   */
  private buildFieldsToSave(
    v: ReturnType<EhrVendorSourceFormComponent['form']['getRawValue']>,
    aud: EpicAudience,
    cfg: AudienceFieldConfig,
    emitRecurrence: boolean,
  ): Record<string, string> {
    return {
      // Lets NodeLibraryDialogComponent re-open the correct per-vendor wrapper when editing an existing canvas
      // node later (see its _sourceFormKeyForNode) — the same pattern GenericFhirSourceFormComponent/
      // Hl7v2SourceFormComponent already use their own 'Connector' value for. Portal-side bookkeeping only:
      // WorkflowBuildAssemblerService.buildSource() still resolves every non-GenericFhir/non-Sample connector to
      // sourceSystemType 'Epic' regardless of this value (pre-existing — canvas-mode workflow build has only ever
      // actually assembled Epic connections; wiring non-Epic vendors all the way through canvas → workflow-build
      // is tracked as follow-up work, not part of this UI-layer split).
      'Connector':             this.vendor(),
      'Client ID':             v.clientId ?? '',
      // Only meaningful when the audience is on Client Secret auth (showSecret()) — WizardService.save() treats
      // a blank value here as "leave whatever secret is already stored untouched" (existingClientSecretRef),
      // not "clear the secret", so this is safe to always include even when the field is hidden/cleared.
      'Client Secret':         v.clientSecret ?? '',
      // athenahealth only — bare numeric practice id; the backend builds the ah-practice reference from it.
      // Empty for every other vendor (showPracticeId() gates both visibility and requiredness).
      'Practice ID':           v.practiceId ?? '',
      'Auth method':           v.authMethod ?? 'secret',
      // Only meaningful for Backend System + JWT — lets a later "was this key FHIRBridge-provisioned?" check (e.g.
      // WorkflowBuilderComponent auto-filling the real JWKS URL after build assigns a sourceConnectionId) tell a
      // generated/imported key apart from one pointing at an externally-hosted JWKS, without re-deriving it from
      // the Key Vault Name/Secret Name values alone (which look identical either way).
      'Signing key source':    v.keySource ?? 'manual',
      // Only meaningful for Client Secret auth — see the authPlacement control's own remarks.
      'Auth placement':        v.authPlacement ?? 'post',
      'Epic audience':         aud,
      'SMART version':         'SMART App Launch 2.0 (R4)',
      'Scope version':         v.scopeVersion === 'v1' ? 'v1 (coarse)' : 'v2 (granular)',
      // The actual, discovery-validated scope string this form built and showed the user — takes priority over
      // WizardService.save()'s own ScopeBuilderService-derived default (which knows nothing about the selected
      // resources, scope version, or audience-specific scopes this form computed).
      'Scopes':                this.scopeString().split(/\s+/).filter(Boolean).join(' '),
      // Backend System only: whatever Epic's token response actually granted on the last successful Discover
      // (see runBackendAuthScopeProbe) — distinct from 'Scopes' above, which is what we requested. Empty when
      // Discover hasn't run or the probe hasn't succeeded yet.
      'Discovered scopes':     this.grantedScopesStatus() === 'done' ? this.grantedScopes().join(' ') : '',
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
          'Full refresh time zone':     v.fullRefreshTimeZone || detectBrowserTimeZone(),
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
    };
  }

  /**
   * SourceConfigFormComponent contract — validates the form and returns the same `fields` bag save() would write
   * (minus the canvas-mode-only "reuse vs fork an existing connection" bookkeeping, which needs save()'s own
   * side-effecting resolution against `existingConnections()`/WizardService and doesn't apply to a generic
   * "read the form back out" caller). This form's own footer Save button still goes through save() below, which
   * drives the real WizardService-backed create/update flow end to end — getFields() exists so this component can
   * also be hosted anywhere else a plain "give me the current fields, or null if invalid" contract is expected,
   * mirroring GenericFhirSourceFormComponent.getFields().
   */
  getFields(): Record<string, string> | null {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return null;
    }
    const v   = this.form.getRawValue();
    const aud = v.audience as EpicAudience;
    const cfg = this.audienceConfig();
    const emitRecurrence = v.runMode === 'full'
      || (this.retrievalMethod() === 'bulk-export' && v.exportScope !== '' && v.exportScope !== 'patient');
    return this.buildFieldsToSave(v, aud, cfg, emitRecurrence);
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
    this.wiz.resources.set(this.showResourcePickerSection() ? (v.resources ?? []) : this.activeRetrievalResourceTypes());

    const formValuesToSave = {
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
    };
    const fieldsToSave: Record<string, string> = {
      ...this.buildFieldsToSave(v, aud, cfg, emitRecurrence),
      // Reused-as-is "Existing Source" pick (see resolvedSourceConnectionId above): tells
      // WorkflowBuildAssemblerService.assemble() to skip this node's Sources spec entirely and let the backend
      // resolve sourceConnectionId straight off this node's own config, same as destinationResolved for destinations.
      ...(resolvedSourceConnectionId ? {
        sourceConnectionId: resolvedSourceConnectionId,
        sourceConnectionResolved: 'true',
      } : {}),
    };

    console.log('%c[Epic Configuration] "Add to Pipeline" clicked — node data about to be saved:', 'color:#00A89D;font-weight:700');
    console.log('formValues (connection basics):', formValuesToSave);
    console.log('fields (everything else stored on the node):', fieldsToSave);

    // Subscribe BEFORE calling save() — it fires synchronously on success/failure once the HTTP call
    // settles, and save() itself doesn't return anything to await. Only close the dialog (via `saved`)
    // once the backend actually confirms success; on failure, surface the real error under App Name
    // (the field the user needs to change to retry) and keep everything else exactly as they left it.
    this.wiz.saveOutcome$.pipe(take(1)).subscribe(outcome => {
      if (outcome.success) {
        this.saved.emit();
        return;
      }
      this.saveErrorMessage.set(outcome.error ?? 'Save failed.');
      this.form.controls.appName.setErrors({ server: true });
      this.form.controls.appName.markAsTouched();
      queueMicrotask(() => {
        const el = this.formRoot?.nativeElement.querySelector<HTMLInputElement>('#eaf-appName');
        el?.focus();
        el?.select();
      });
    });
    this.wiz.save(formValuesToSave, fieldsToSave);
  }

  /** Backs both the topbar "← Back to library" and the footer "Cancel" buttons — same confirm-before-discard
   *  prompt the rest of the app uses for routed pages (UnsavedChangesPromptService), just invoked directly
   *  here since this form is an inline panel inside NodeLibraryDialogComponent, not its own route. */
  protected cancel(): void {
    this.unsavedChangesPrompt.confirmLeave(this).subscribe(canLeave => {
      if (canLeave) this.cancelled.emit();
    });
  }

  /** HasUnsavedChanges — form.dirty flips true the moment any control is edited by the user (via
   *  typing/selecting), regardless of whether this is a brand-new node or an in-place edit. Disabled
   *  (read-only view) controls can never become dirty, so no separate readonly check is needed. */
  hasUnsavedChanges(): boolean {
    return this.form.dirty;
  }

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
