import { Component, input, signal, computed, effect, untracked, inject } from '@angular/core';
import { FormBuilder, Validators, ReactiveFormsModule } from '@angular/forms';
import { FHIR_RESOURCES } from '../../../data/scope-constants.data';

export type GenericFhirRetrievalMethod = 'search-rest' | 'bulk-export';

/**
 * Minimal source-config form for a bare, unauthenticated conformant FHIR R4 server (SourceSystemType.GenericFhir)
 * — Name, Base URL, which FHIR resources to pull, and a retrieval method (Search REST or Bulk $export). Excludes
 * every OAuth/interactive concept EpicAudienceFormComponent handles (audiences, launch context, key generation,
 * subscription/webhook/CDS Hooks) — a generic FHIR endpoint has none of that; only AuthenticationType.None is
 * supported today. Follows the same "host reads getFields(), null means invalid" contract as
 * DestinationConnectionFormComponent.getConfig() / MappingProfileFormComponent.getRows().
 *
 * Field keys below are deliberately the exact ones EpicAudienceFormComponent's Backend-System retrieval section
 * already writes (see workflow-build-assembler.service.ts's buildRetrieval()), so that one vendor-agnostic method
 * builds the SourceRetrievalConfigurationRequest for both — search-rest's _since/_lastUpdated incremental cursor,
 * _count, _sort, _include/_revinclude, and bulk $export's scope/group/patient/output-format all flow through
 * unchanged; nothing about that method is Epic-specific.
 */
@Component({
  selector: 'app-generic-fhir-source-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './generic-fhir-source-form.component.html',
  styleUrl: './generic-fhir-source-form.component.scss',
})
export class GenericFhirSourceFormComponent {
  private readonly fb = inject(FormBuilder);

  /** dest_*-style field bag from an existing node's config, when editing one already on the canvas. */
  readonly initialFields = input<Record<string, string> | null>(null);

  readonly form = this.fb.group({
    name: ['Generic FHIR R4', [Validators.required]],
    baseUrl: ['', [Validators.required]],
    retrievalMethod: ['search-rest' as GenericFhirRetrievalMethod],
    // ── Search REST ──────────────────────────────────────────────────────────
    searchCriteria: [''],
    incrementalCursor: [false],
    pageSize: [''],
    sortOrder: [''],
    includeParams: [''],
    revIncludeParams: [''],
    // ── Bulk $export ─────────────────────────────────────────────────────────
    exportScope: ['system' as 'system' | 'patient' | 'group'],
    groupId: [''],
    patientIds: [''],
    // ── Shared ────────────────────────────────────────────────────────────────
    retryPolicy: [''],
    timeoutSeconds: [''],
    maxRecordsPerRun: [''],
  });

  readonly isBulkExport = computed(() => this.form.controls.retrievalMethod.value === 'bulk-export');

  readonly availableResources = FHIR_RESOURCES;
  readonly selectedResources = signal<string[]>([]);

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
      name: fields['__name'] || 'Generic FHIR R4',
      baseUrl: fields['FHIR base URL'] || '',
      retrievalMethod: (fields['Retrieval method key'] as GenericFhirRetrievalMethod) || 'search-rest',
      searchCriteria: fields['Search criteria'] || '',
      incrementalCursor: fields['Incremental cursor'] === 'enabled',
      pageSize: fields['Page size (_count)'] || '',
      sortOrder: fields['Sort (_sort)'] || '',
      includeParams: fields['Include (_include)'] || '',
      revIncludeParams: fields['Reverse include (_revinclude)'] || '',
      exportScope: (fields['Export scope'] as 'system' | 'patient' | 'group') || 'system',
      groupId: fields['Group ID'] || '',
      patientIds: fields['Patient ID / list'] || '',
      retryPolicy: fields['Retry policy'] || '',
      timeoutSeconds: fields['Timeout (seconds)'] || '',
      maxRecordsPerRun: fields['Max records per run'] || '',
    });
    if (fields['Resources']) {
      this.selectedResources.set(fields['Resources'].split(',').map(r => r.trim()).filter(Boolean));
    }
  }

  isResourceSelected(r: string): boolean {
    return this.selectedResources().includes(r);
  }

  toggleResource(r: string): void {
    this.selectedResources.update(list =>
      list.includes(r) ? list.filter(x => x !== r) : [...list, r]);
  }

  /** null ⇒ fix the highlighted fields — mirrors getConfig()/getRows()'s "null means invalid" contract. */
  getFields(): Record<string, string> | null {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.selectedResources().length === 0) return null;

    const v = this.form.getRawValue();
    const fields: Record<string, string> = {
      __name: v.name || 'Generic FHIR R4',
      Connector: 'Generic FHIR R4',
      'App context': 'Backend system',
      'Ingestion mode': 'search',
      'FHIR base URL': v.baseUrl || '',
      Resources: this.selectedResources().join(','),
      'Retrieval method key': v.retrievalMethod ?? 'search-rest',
      'Retry policy': v.retryPolicy || '',
      'Timeout (seconds)': v.timeoutSeconds || '',
      'Max records per run': v.maxRecordsPerRun || '',
    };

    if (v.retrievalMethod === 'bulk-export') {
      fields['Export scope'] = v.exportScope ?? 'system';
      if (v.exportScope === 'group') fields['Group ID'] = v.groupId || '';
      if (v.exportScope === 'patient') fields['Patient ID / list'] = v.patientIds || '';
      fields['FHIR output format'] = 'ndjson';
    } else {
      fields['Search criteria'] = v.searchCriteria || '';
      fields['Incremental cursor'] = v.incrementalCursor ? 'enabled' : '';
      fields['Page size (_count)'] = v.pageSize || '';
      fields['Sort (_sort)'] = v.sortOrder || '';
      fields['Include (_include)'] = v.includeParams || '';
      fields['Reverse include (_revinclude)'] = v.revIncludeParams || '';
    }

    return fields;
  }
}
