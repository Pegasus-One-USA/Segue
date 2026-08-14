import { Component, input, signal, computed, effect, untracked, inject } from '@angular/core';
import { FormBuilder, Validators, ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { FHIR_RESOURCES } from '../../../data/scope-constants.data';
import { SourceConfigFormComponent } from '../../shared/config-form/config-form.contract';
import {
  detectBrowserTimeZone,
  TIME_ZONE_OPTIONS,
  POLL_FREQUENCY_OPTIONS,
  SORT_OPTIONS,
  RETRY_POLICY_OPTIONS,
  FULL_REFRESH_RECURRENCE_OPTIONS,
  WEEKDAY_OPTIONS,
  RECONCILIATION_OPTIONS,
  ENDPOINT_TYPE_OPTIONS,
  EVENT_TYPE_OPTIONS,
} from '../../shared/ehr-vendor-source-form/ehr-vendor-source-form.component';

export type GenericFhirRetrievalMethod = 'subscription' | 'webhook' | 'search-rest' | 'bulk-export';
export type GenericFhirRunMode = 'incremental' | 'full' | 'manual';
export type GenericFhirExportScope = 'system' | 'group' | 'patient';

const RETRIEVAL_METHOD_OPTIONS: readonly { value: GenericFhirRetrievalMethod; label: string; description: string }[] = [
  { value: 'subscription', label: 'Subscription', description: 'The server pushes change notifications through a standard FHIR Subscription (rest-hook/websocket).' },
  { value: 'webhook', label: 'Webhook', description: 'The server (or a middleware relay) posts updates to a callback endpoint.' },
  { value: 'search-rest', label: 'Search (REST)', description: 'FHIRBridge polls the server’s FHIR REST search API on a schedule.' },
  { value: 'bulk-export', label: 'Bulk Export', description: 'Kicks off a FHIR Bulk Data $export job and retrieves the resulting NDJSON files.' },
];

/**
 * Source-config form for a bare, unauthenticated conformant FHIR R4 server (SourceSystemType.GenericFhir) —
 * deliberately mirrors EpicAudienceFormComponent's "Data Retrieval Method" + "Retrieval Configuration" sections
 * field-for-field and class-for-class (same eaf-* styles, imported from that component's own stylesheet) so this
 * form looks and behaves identically for the four retrieval methods Epic's form offers. Excludes every
 * OAuth/interactive/EHR-specific concept Epic's form also carries (audiences, launch context, key generation, CDS
 * Hooks) — none of that applies to a generic, anonymous FHIR endpoint; only AuthenticationType.None is supported
 * today. Subscription/Webhook are offered for the same reason Epic's form offers them even though
 * SourceNodeExecutor (Runtime plane) only actually executes search-rest/bulk-export today — matching Epic's own
 * current UI/backend gap rather than under- or over-promising relative to it.
 *
 * Field keys below are deliberately identical to the ones EpicAudienceFormComponent's save() already writes (see
 * that file's `this.wiz.save(...)` call and workflow-build-assembler.service.ts's buildRetrieval()) — same
 * 'Retrieval method key' / 'Run mode' / 'Schedule / poll frequency' / 'Full refresh schedule (cron)' / etc. This
 * is what lets workflow-builder.component.ts's backendRetrievalFields()/buildTrigger() derive this workflow's
 * schedule from this form exactly the way it already does for Epic — no separate wiring needed for Generic FHIR.
 * Follows the same "host reads getFields(), null means invalid" contract as
 * DestinationConnectionFormComponent.getConfig() / MappingProfileFormComponent.getRows().
 */
@Component({
  selector: 'app-generic-fhir-source-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './generic-fhir-source-form.component.html',
  styleUrls: [
    '../../shared/ehr-vendor-source-form/ehr-vendor-source-form.component.scss',
    './generic-fhir-source-form.component.scss',
  ],
})
export class GenericFhirSourceFormComponent implements SourceConfigFormComponent {
  private readonly fb = inject(FormBuilder);

  /** dest_*-style field bag from an existing node's config, when editing one already on the canvas. */
  readonly initialFields = input<Record<string, string> | null>(null);

  readonly retrievalMethodOptions = RETRIEVAL_METHOD_OPTIONS;
  readonly pollFrequencyOptions = POLL_FREQUENCY_OPTIONS;
  readonly sortOptions = SORT_OPTIONS;
  readonly retryPolicyOptions = RETRY_POLICY_OPTIONS;
  readonly fullRefreshRecurrenceOptions = FULL_REFRESH_RECURRENCE_OPTIONS;
  readonly weekdayOptions = WEEKDAY_OPTIONS;
  readonly timeZoneOptions = TIME_ZONE_OPTIONS;
  readonly reconciliationOptions = RECONCILIATION_OPTIONS;
  readonly endpointTypeOptions = ENDPOINT_TYPE_OPTIONS;
  readonly eventTypeOptions = EVENT_TYPE_OPTIONS;
  readonly notificationPayloadOptions = [
    { value: 'id-only', label: 'ID only' },
    { value: 'full', label: 'Full resource' },
    { value: 'empty', label: 'Empty (ping only)' },
  ];
  readonly payloadFormatOptions = [
    { value: 'fhir-json', label: 'FHIR JSON' },
    { value: 'fhir-xml', label: 'FHIR XML' },
  ];

  readonly form = this.fb.group({
    name: ['Generic FHIR R4', [Validators.required]],
    baseUrl: ['', [Validators.required]],
    retrievalMethod: ['search-rest' as GenericFhirRetrievalMethod],
    // ── Subscription / Webhook ────────────────────────────────────────────────
    eventType: [''],
    notificationPayload: ['id-only'],
    endpointType: ['rest-hook'],
    payloadFormat: ['fhir-json'],
    reconciliationSchedule: [''],
    // ── Search REST ──────────────────────────────────────────────────────────
    searchCriteria: [''],
    runMode: ['manual' as GenericFhirRunMode],
    schedulePollFrequency: [''],
    // ── Bulk $export ─────────────────────────────────────────────────────────
    exportScope: ['system' as GenericFhirExportScope],
    groupId: [''],
    patientIds: [''],
    fhirOutputFormat: ['ndjson' as 'ndjson' | 'ndjson-gzip'],
    // ── Shared: Incremental Cursor (search-rest + bulk-export read the same flag) ─
    incrementalCursor: [false],
    // ── Full Refresh / Bulk Export calendar recurrence ───────────────────────
    fullRefreshRecurrence: ['daily' as 'daily' | 'weekly' | 'monthly'],
    fullRefreshDaysOfWeek: [[] as string[]],
    fullRefreshDayOfMonth: ['1'],
    fullRefreshTime: ['02:00'],
    fullRefreshTimeZone: [detectBrowserTimeZone()],
    // ── Advanced Search Options (Search REST only) ────────────────────────────
    pageSize: ['100'],
    sortOrder: [''],
    includeParams: [''],
    revIncludeParams: [''],
    retryPolicy: ['exponential'],
    timeoutSeconds: ['30'],
    maxRecordsPerRun: [''],
  });

  private readonly retrievalMethodValue = toSignal(this.form.controls.retrievalMethod.valueChanges, { initialValue: this.form.controls.retrievalMethod.value });
  private readonly runModeValue = toSignal(this.form.controls.runMode.valueChanges, { initialValue: this.form.controls.runMode.value });
  private readonly exportScopeValue = toSignal(this.form.controls.exportScope.valueChanges, { initialValue: this.form.controls.exportScope.value });
  private readonly fullRefreshRecurrenceValue = toSignal(this.form.controls.fullRefreshRecurrence.valueChanges, { initialValue: this.form.controls.fullRefreshRecurrence.value });
  private readonly fullRefreshDaysOfWeekValue = toSignal(this.form.controls.fullRefreshDaysOfWeek.valueChanges, { initialValue: this.form.controls.fullRefreshDaysOfWeek.value });
  private readonly fullRefreshDayOfMonthValue = toSignal(this.form.controls.fullRefreshDayOfMonth.valueChanges, { initialValue: this.form.controls.fullRefreshDayOfMonth.value });
  private readonly fullRefreshTimeValue = toSignal(this.form.controls.fullRefreshTime.valueChanges, { initialValue: this.form.controls.fullRefreshTime.value });
  private readonly fullRefreshTimeZoneValue = toSignal(this.form.controls.fullRefreshTimeZone.valueChanges, { initialValue: this.form.controls.fullRefreshTimeZone.value });

  readonly retrievalMethod = computed(() => this.retrievalMethodValue() ?? 'search-rest');
  readonly retrievalMethodConfig = computed(() => this.retrievalMethodOptions.find(m => m.value === this.retrievalMethod()));
  readonly isBulkExport = computed(() => this.retrievalMethod() === 'bulk-export');
  readonly isSearchRest = computed(() => this.retrievalMethod() === 'search-rest');
  readonly isSubscription = computed(() => this.retrievalMethod() === 'subscription');
  readonly isWebhook = computed(() => this.retrievalMethod() === 'webhook');
  readonly isPushBased = computed(() => this.isSubscription() || this.isWebhook());

  /** Search REST Full Refresh, or a System/Group bulk export — both schedule on the same calendar "Repeat"
   *  control. A Patient-id-list export is a one-off and never shows/needs a recurrence. */
  readonly showRecurrence = computed(() =>
    this.isBulkExport() ? this.exportScopeValue() !== 'patient' : this.isSearchRest() && this.runModeValue() === 'full');

  readonly showPollFrequency = computed(() => this.isSearchRest() && this.runModeValue() !== 'full');

  /** Incremental Sync is meaningless without the _lastUpdated/_since cursor, so Run Mode = Incremental Sync forces
   *  it on — mirrors EpicAudienceFormComponent.incrementalCursorLocked. */
  readonly incrementalCursorLocked = computed(() => this.isSearchRest() && this.runModeValue() === 'incremental');

  readonly advancedOptionsOpen = signal(false);
  toggleAdvancedOptions(): void { this.advancedOptionsOpen.update(v => !v); }

  /** Google-Calendar-style cron expression for Run Mode = Full Refresh / a scheduled bulk export — identical
   *  algorithm to EpicAudienceFormComponent.fullRefreshCronExpression. */
  readonly fullRefreshCronExpression = computed(() => {
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

  readonly fullRefreshCronSummary = computed(() => {
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

  // Resource Type is not asked in this form's own UI — the destination node's own "dest_resources" picker already
  // captures it, and SourceNodeExecutor derives it from there when a source node has none of its own. Defaults to
  // every MVP1 resource type (same fallback EpicAudienceFormComponent.ensureRetrievalResourceTypeDefault uses for
  // its own now-hidden per-method Resource Type controls) so the emitted 'Resources' field is never empty; a node
  // restored from a previously saved value keeps that value instead (see _populate).
  readonly selectedResources = signal<string[]>([...FHIR_RESOURCES]);

  constructor() {
    effect(() => {
      // Incremental Sync forces the cursor checkbox on/locked, and clears the poll-frequency requirement gate.
      if (this.incrementalCursorLocked()) {
        untracked(() => this.form.controls.incrementalCursor.setValue(true, { emitEvent: false }));
      }
    });

    effect(() => {
      const fields = this.initialFields();
      untracked(() => {
        if (fields) this._populate(fields);
      });
    });
  }

  private _populate(fields: Record<string, string>): void {
    this.form.patchValue({
      name: fields['__name'] || 'Generic FHIR R4',
      baseUrl: fields['FHIR base URL'] || '',
      retrievalMethod: (fields['Retrieval method key'] as GenericFhirRetrievalMethod) || 'search-rest',
      eventType: fields['Event type'] || '',
      notificationPayload: fields['Notification payload'] || 'id-only',
      endpointType: fields['Endpoint type'] || 'rest-hook',
      payloadFormat: fields['Payload format'] || 'fhir-json',
      reconciliationSchedule: fields['Reconciliation schedule'] || '',
      searchCriteria: fields['Search criteria'] || '',
      runMode: (fields['Run mode'] as GenericFhirRunMode) || 'manual',
      schedulePollFrequency: fields['Schedule / poll frequency'] || '',
      exportScope: (fields['Export scope'] as GenericFhirExportScope) || 'system',
      groupId: fields['Group ID'] || '',
      patientIds: fields['Patient ID / list'] || '',
      fhirOutputFormat: (fields['FHIR output format'] as 'ndjson' | 'ndjson-gzip') || 'ndjson',
      incrementalCursor: fields['Incremental cursor'] === 'enabled',
      fullRefreshRecurrence: (fields['Full refresh recurrence'] as 'daily' | 'weekly' | 'monthly') || 'daily',
      fullRefreshDaysOfWeek: fields['Full refresh days of week'] ? fields['Full refresh days of week'].split(',').filter(Boolean) : [],
      fullRefreshDayOfMonth: fields['Full refresh day of month'] || '1',
      fullRefreshTime: fields['Full refresh time'] || '02:00',
      fullRefreshTimeZone: fields['Full refresh time zone'] || detectBrowserTimeZone(),
      pageSize: fields['Page size (_count)'] || '',
      sortOrder: fields['Sort (_sort)'] || '',
      includeParams: fields['Include (_include)'] || '',
      revIncludeParams: fields['Reverse include (_revinclude)'] || '',
      retryPolicy: fields['Retry policy'] || '',
      timeoutSeconds: fields['Timeout (seconds)'] || '',
      maxRecordsPerRun: fields['Max records per run'] || '',
    });
    if (fields['Resources']) {
      this.selectedResources.set(fields['Resources'].split(',').map(r => r.trim()).filter(Boolean));
    }
  }

  toggleWeekday(day: string): void {
    this.form.controls.fullRefreshDaysOfWeek.setValue(
      this.selectedWeekdays().includes(day)
        ? this.selectedWeekdays().filter(d => d !== day)
        : [...this.selectedWeekdays(), day],
    );
  }
  readonly selectedWeekdays = computed(() => this.fullRefreshDaysOfWeekValue() ?? []);

  /** null ⇒ fix the highlighted fields — mirrors getConfig()/getRows()'s "null means invalid" contract. */
  getFields(): Record<string, string> | null {
    this.form.markAllAsTouched();
    if (this.form.invalid) return null;
    if (this.showRecurrence() && this.form.controls.fullRefreshRecurrence.value === 'weekly' && this.selectedWeekdays().length === 0) return null;

    const v = this.form.getRawValue();
    const resourcesJoined = this.selectedResources().join(', ');
    const fields: Record<string, string> = {
      __name: v.name || 'Generic FHIR R4',
      Connector: 'Generic FHIR R4',
      'App context': 'Backend system',
      'Ingestion mode': 'search',
      'FHIR base URL': v.baseUrl || '',
      Resources: this.selectedResources().join(','),
      'Retrieval resource type': resourcesJoined,
      'Retrieval method key': v.retrievalMethod ?? 'search-rest',
    };

    if (this.isPushBased()) {
      fields['Event type'] = v.eventType || '';
      fields['Endpoint type'] = v.endpointType || '';
      fields['Reconciliation schedule'] = v.reconciliationSchedule || '';
      if (this.isSubscription()) {
        fields['Notification payload'] = v.notificationPayload || '';
      } else {
        fields['Payload format'] = v.payloadFormat || '';
      }
      return fields;
    }

    fields['Incremental cursor'] = v.incrementalCursor ? 'enabled' : 'disabled';
    fields['Page size (_count)'] = v.pageSize || '';
    fields['Sort (_sort)'] = v.sortOrder || '';
    fields['Include (_include)'] = v.includeParams || '';
    fields['Reverse include (_revinclude)'] = v.revIncludeParams || '';
    fields['Retry policy'] = v.retryPolicy || '';
    fields['Timeout (seconds)'] = v.timeoutSeconds || '';
    fields['Max records per run'] = v.maxRecordsPerRun || '';

    if (this.isBulkExport()) {
      fields['Export scope'] = v.exportScope ?? 'system';
      if (v.exportScope === 'group') fields['Group ID'] = v.groupId || '';
      if (v.exportScope === 'patient') fields['Patient ID / list'] = v.patientIds || '';
      fields['FHIR output format'] = v.fhirOutputFormat ?? 'ndjson';
    } else {
      fields['Search criteria'] = v.searchCriteria || '';
      fields['Run mode'] = v.runMode ?? 'manual';
      fields['Schedule / poll frequency'] = v.schedulePollFrequency || '';
    }

    if (this.showRecurrence()) {
      fields['Full refresh recurrence'] = v.fullRefreshRecurrence ?? 'daily';
      fields['Full refresh days of week'] = (v.fullRefreshDaysOfWeek ?? []).join(',');
      fields['Full refresh day of month'] = v.fullRefreshDayOfMonth ?? '1';
      fields['Full refresh time'] = v.fullRefreshTime ?? '02:00';
      fields['Full refresh time zone'] = v.fullRefreshTimeZone || detectBrowserTimeZone();
      fields['Full refresh schedule (cron)'] = this.fullRefreshCronExpression() ?? '';
    }

    return fields;
  }
}
