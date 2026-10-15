import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { HttpErrorResponse } from '@angular/common/http';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import {
  WorkflowApiService,
  WorkflowRunConnectionValues,
  WorkflowRunEhrEndpointOption,
  WorkflowRunOptions,
  ExecuteV2RunOptions,
} from '../../../services/workflow-api.service';

export interface ExecuteV2DialogData {
  workflowId: string;
  workflowName: string;
}

interface ResourceCriteriaRow {
  resourceType: string;
  criteria: string;
  /** Saved criteria for this type ('' when the row was added just for this run). */
  saved: string;
}

interface SettingRow {
  label: string;
  current: string;
  next: string;
  changed: boolean;
}

/**
 * "Execute V2": run a workflow once against a chosen hospital (EHR Endpoint). Opens pre-selected on the hospital the
 * workflow is configured for today, shows precisely which connection settings would change if another one is picked,
 * and only then offers Execute. The saved workflow and source connection are never modified.
 */
@Component({
  selector: 'app-execute-v2-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './execute-v2-dialog.component.html',
  styleUrls: ['./execute-v2-dialog.component.scss'],
})
export class ExecuteV2DialogComponent implements OnInit {
  private readonly api = inject(WorkflowApiService);
  private readonly toast = inject(ToastService);
  readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as ExecuteV2DialogData;

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly executing = signal(false);
  protected readonly options = signal<WorkflowRunOptions | null>(null);

  /** '' = the workflow's saved configuration ("Default"); otherwise an EHR Endpoint id. */
  protected readonly selectedId = signal<string>('');

  /** Editable, pre-populated from the source connection's saved retrieval settings. */
  protected readonly groupId = signal<string>('');
  protected readonly searchCriteria = signal<string>('');
  private savedGroupId = '';
  private savedSearchCriteria = '';

  protected readonly isBulkExport = computed(
    () => (this.options()?.retrieval?.method ?? '').toLowerCase() === 'bulk-export');
  protected readonly groupIdChanged = computed(() => this.groupId().trim() !== this.savedGroupId);
  protected readonly searchCriteriaChanged = computed(() => this.searchCriteria().trim() !== this.savedSearchCriteria);

  /** Per-resource-type criteria for this run, pre-filled from the saved ones (the "Criteria" button). */
  protected readonly resourceRows = signal<ResourceCriteriaRow[]>([]);
  protected readonly newResourceType = signal<string>('');

  protected updateResourceRow(index: number, criteria: string): void {
    this.resourceRows.update(rows => rows.map((r, i) => (i === index ? { ...r, criteria } : r)));
  }

  protected resetResourceRow(index: number): void {
    this.resourceRows.update(rows => rows.map((r, i) => (i === index ? { ...r, criteria: r.saved } : r)));
  }

  protected removeResourceRow(index: number): void {
    this.resourceRows.update(rows => rows.filter((_, i) => i !== index));
  }

  /** The workflow's mapped resource types that don't have a row yet. */
  protected readonly availableResourceTypes = computed(() => {
    const taken = new Set(this.resourceRows().map(r => r.resourceType.toLowerCase()));
    return (this.options()?.resourceTypes ?? []).filter(t => !taken.has(t.toLowerCase()));
  });

  protected addResourceRow(): void {
    const resourceType = this.newResourceType().trim();
    if (!resourceType) return;
    if (!this.resourceRows().some(r => r.resourceType.toLowerCase() === resourceType.toLowerCase())) {
      this.resourceRows.update(rows => [...rows, { resourceType, criteria: '', saved: '' }]);
    }
    this.newResourceType.set('');
  }

  protected resetGroupId(): void { this.groupId.set(this.savedGroupId); }
  protected resetSearchCriteria(): void { this.searchCriteria.set(this.savedSearchCriteria); }

  protected readonly selected = computed<WorkflowRunEhrEndpointOption | null>(() => {
    const id = this.selectedId();
    return id ? this.options()?.endpoints.find(e => e.id === id) ?? null : null;
  });

  /** The hospital the workflow points at today, shown as "Default" in the picker. */
  protected readonly defaultEndpoint = computed(() => {
    const o = this.options();
    return o?.matchedEhrEndpointId ? o.endpoints.find(e => e.id === o.matchedEhrEndpointId) ?? null : null;
  });

  protected readonly rows = computed<SettingRow[]>(() => {
    const o = this.options();
    const target = this.selected();
    if (!o?.defaults) return [];

    const pick = (override: string | null | undefined, fallback: string | null) =>
      override && override.trim() ? override.trim() : fallback;
    const next: WorkflowRunConnectionValues = target
      ? {
          baseUrl: target.baseUrl,
          tokenEndpoint: pick(target.tokenEndpoint, o.defaults.tokenEndpoint),
          clientId: pick(target.clientId, o.defaults.clientId),
          keyId: pick(target.keyId, o.defaults.keyId),
          practiceId: pick(target.practiceId, o.defaults.practiceId),
        }
      : o.defaults;

    const fields: { label: string; key: keyof WorkflowRunConnectionValues }[] = [
      { label: 'FHIR base URL', key: 'baseUrl' },
      { label: 'Token endpoint', key: 'tokenEndpoint' },
      { label: 'Client ID', key: 'clientId' },
      { label: 'Key ID', key: 'keyId' },
      { label: 'Practice ID', key: 'practiceId' },
    ];
    return fields
      .map(f => {
        const current = (o.defaults![f.key] ?? '').trim();
        const value = (next[f.key] ?? '').trim();
        return { label: f.label, current, next: value, changed: !!target && current !== value };
      })
      .filter(r => r.current || r.next);
  });

  protected readonly changedCount = computed(() => this.rows().filter(r => r.changed).length);

  ngOnInit(): void {
    this.api.runOptions(this.data.workflowId).subscribe({
      next: options => {
        this.options.set(options);
        this.savedGroupId = (options.retrieval?.groupId ?? '').trim();
        this.savedSearchCriteria = (options.retrieval?.searchCriteria ?? '').trim();
        this.groupId.set(this.savedGroupId);
        this.searchCriteria.set(this.savedSearchCriteria);
        // Auto-fill: start on the hospital the workflow is already configured for ('' = Default when it matches none).
        this.selectedId.set('');
        this.loading.set(false);
        this.loadSavedResourceCriteria();
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(err.error?.title ?? 'Could not load the run options for this workflow.');
        this.loading.set(false);
      },
    });
  }

  /** Best effort: without view access to the saved criteria the section simply starts empty. */
  private loadSavedResourceCriteria(): void {
    this.api.listResourceTypeCriteria(this.data.workflowId).subscribe({
      next: saved => {
        const byType = new Map<string, ResourceCriteriaRow>();
        for (const row of saved) {
          const key = row.resourceType.toLowerCase();
          if (row.criteria?.trim() && !byType.has(key)) {
            const criteria = row.criteria.trim();
            byType.set(key, { resourceType: row.resourceType, criteria, saved: criteria });
          }
        }
        this.resourceRows.set([...byType.values()].sort((a, b) => a.resourceType.localeCompare(b.resourceType)));
      },
      error: () => this.resourceRows.set([]),
    });
  }

  protected execute(): void {
    if (this.executing()) return;
    const options = this.options();
    if (!options) return;
    if (!options.supportsHospitalSwitch && this.selected()) return;

    // Group ID applies to a bulk export, Search Criteria to a search-REST workflow; only the applicable one is sent,
    // and only when the user changed it. An untouched field sent as "" told the server "clear it for this run", so an
    // ordinary run was recorded as an overridden one.
    const v2: ExecuteV2RunOptions = { targetEhrEndpointId: this.selected()?.id ?? null };
    if (options.supportsRunCriteria) {
      if (this.isBulkExport()) {
        if (this.groupIdChanged()) v2.groupIdOverride = this.groupId().trim();
      } else if (this.searchCriteriaChanged()) {
        v2.searchCriteriaOverride = this.searchCriteria().trim();
      }
    }

    // Only rows the user changed or added are sent; an untouched row keeps using its saved criteria server-side.
    const resourceCriteria: Record<string, string> = {};
    for (const row of this.resourceRows()) {
      const criteria = row.criteria.trim();
      if (criteria && criteria !== row.saved) resourceCriteria[row.resourceType] = criteria;
    }
    if (Object.keys(resourceCriteria).length > 0) v2.resourceCriteriaOverrides = resourceCriteria;

    this.executing.set(true);
    this.api.run(this.data.workflowId, true, v2).subscribe({
      next: () => {
        this.executing.set(false);
        const target = this.selected()?.name ?? 'its default configuration';
        this.toast.success('Running in background', `"${this.data.workflowName}" is running against ${target}.`);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.executing.set(false);
        this.toast.error('Workflow run', err.error?.error ?? err.error?.title ?? 'The run could not be started.');
      },
    });
  }
}
