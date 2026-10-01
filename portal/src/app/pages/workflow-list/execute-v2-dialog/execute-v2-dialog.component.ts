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
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(err.error?.title ?? 'Could not load the run options for this workflow.');
        this.loading.set(false);
      },
    });
  }

  protected execute(): void {
    if (this.executing()) return;
    const options = this.options();
    if (!options) return;
    if (!options.supportsHospitalSwitch && this.selected()) return;

    // Group ID applies to a bulk export, Search Criteria to a search-REST workflow; only the applicable one is sent.
    const v2: ExecuteV2RunOptions = { targetEhrEndpointId: this.selected()?.id ?? null };
    if (options.supportsRunCriteria) {
      if (this.isBulkExport()) v2.groupIdOverride = this.groupId().trim();
      else v2.searchCriteriaOverride = this.searchCriteria().trim();
    }

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
