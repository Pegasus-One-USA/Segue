import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorLogSettings, ErrorLogStorage } from '../../models/operations.model';

/** Plain-language help for each entry type / category, shown beside its checkbox. */
const SEVERITY_HELP: Record<string, string> = {
  Critical: 'Failures that stop a process or a whole run.',
  Error: 'Something failed and needs attention.',
  Warning: 'Something looks wrong but work continued. Recorded from the application\'s own log lines - can be numerous.',
  WorkflowDebug: 'A step-by-step trace of every workflow run: one entry per step (e.g. "Step 2/5 [Source] ... started / completed / FAILED") naming the workflow, its id and the execution id. Use it to find exactly where a run broke. Kept out of the normal error lists; view it by searching the correlation ID of a run.',
  Information: 'Routine progress messages from the application\'s own log lines. Can be very numerous - enable only while investigating.',
};

const CATEGORY_HELP: Record<string, string> = {
  Unknown: 'Could not be classified',
  Business: 'Business-rule problems',
  Validation: 'Invalid or missing data',
  Infrastructure: 'Internal infrastructure problems',
  Authentication: 'Sign-in and token problems',
  Authorization: 'Permission problems',
  Database: 'Database problems',
  Network: 'Connectivity problems',
  ExternalSystem: 'Problems reported by connected systems (EHR, destinations)',
};

@Component({
  selector: 'app-error-log-settings',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './error-log-settings.component.html',
  styleUrl: './error-log-settings.component.scss',
})
export class ErrorLogSettingsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(DialogService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly purging = signal(false);
  readonly severities = signal<string[]>([]);
  readonly categories = signal<string[]>([]);
  readonly storage = signal<ErrorLogStorage | null>(null);

  readonly selectedSeverities = signal<string[]>([]);
  readonly selectedCategories = signal<string[]>([]);
  readonly autoClear = signal(false);
  readonly retentionDays = signal(180);
  readonly minRetentionDays = signal(1);
  readonly workflowDebugDetail = signal('Steps');

  readonly detailOptions: { value: string; label: string; help: string }[] = [
    { value: 'Steps', label: 'Steps', help: 'One line per workflow step: started, completed or FAILED.' },
    { value: 'Stages', label: 'Steps and stages', help: 'Also the stages inside a step: source fetch per resource type, mapping, transformation and each destination write - plus a snapshot of the configuration in use (source settings, field mappings, transformation rules, destination settings). Never includes credentials, patient data, search values or recipient addresses.' },
    { value: 'Resources', label: 'Steps, stages and individual resources', help: 'Also lines for individual resources (type and id): fetched, mapped, transformed, and every record that failed to write. Capped per run. Most detailed - use briefly.' },
  ];

  readonly nothingSelected = computed(() => this.selectedSeverities().length === 0 || this.selectedCategories().length === 0);
  readonly lowSeverityOn = computed(() => this.selectedSeverities().some(s => s === 'Warning' || s === 'Information'));
  readonly workflowDebugOn = computed(() => this.selectedSeverities().includes('WorkflowDebug'));

  severityHelp(name: string): string { return SEVERITY_HELP[name] ?? ''; }
  categoryHelp(name: string): string { return CATEGORY_HELP[name] ?? ''; }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.errorLogSettings().subscribe({
      next: response => {
        this.severities.set(response.availableSeverities);
        this.categories.set(response.availableCategories);
        this.minRetentionDays.set(response.minimumRetentionDays ?? 1);
        this.apply(response.settings);
        this.storage.set(response.storage);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private apply(settings: ErrorLogSettings): void {
    this.selectedSeverities.set([...settings.captureSeverities]);
    this.selectedCategories.set([...settings.captureCategories]);
    this.autoClear.set(settings.autoClearEnabled);
    this.retentionDays.set(settings.retentionDays);
    this.workflowDebugDetail.set(settings.workflowDebugDetail || 'Steps');
  }

  toggle(list: 'severity' | 'category', name: string, checked: boolean): void {
    const target = list === 'severity' ? this.selectedSeverities : this.selectedCategories;
    target.update(current => checked ? [...new Set([...current, name])] : current.filter(x => x !== name));
  }

  setDays(raw: string): void {
    const value = Math.floor(Number(raw));
    this.retentionDays.set(Number.isFinite(value) ? Math.min(3650, Math.max(this.minRetentionDays(), value)) : Math.max(this.minRetentionDays(), 90));
  }

  save(): void {
    if (!this.actionGuard.ensure('governance.write', 'You do not have permission to change the error log settings.')) { return; }
    this.saving.set(true);
    this.api.saveErrorLogSettings({
      captureSeverities: this.selectedSeverities(),
      captureCategories: this.selectedCategories(),
      autoClearEnabled: this.autoClear(),
      retentionDays: this.retentionDays(),
      workflowDebugDetail: this.workflowDebugDetail(),
    }).subscribe({
      next: response => {
        this.apply(response.settings);
        this.storage.set(response.storage);
        this.saving.set(false);
        this.toast.success('Error log settings saved.');
      },
      error: () => this.saving.set(false),
    });
  }

  confirmPurge(): void {
    if (!this.actionGuard.ensure('governance.delete', 'You do not have permission to delete error log entries.')) { return; }
    this.dialogs
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '460px',
        data: {
          title: 'Delete old error log entries',
          message: `This permanently deletes every error log entry older than ${this.retentionDays()} day(s) (using the saved retention period). This cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) { return; }
        this.purging.set(true);
        this.api.purgeErrorLog().subscribe({
          next: result => {
            this.purging.set(false);
            this.toast.success(`${result.deleted} entr${result.deleted === 1 ? 'y' : 'ies'} deleted.`);
            this.api.errorLogStorage().subscribe({ next: s => this.storage.set(s) });
          },
          error: () => this.purging.set(false),
        });
      });
  }

  formatBytes(bytes: number | null | undefined): string {
    if (bytes === null || bytes === undefined) { return 'not available'; }
    if (bytes < 1024) { return `${bytes} B`; }
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024;
    let i = 0;
    while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
    return `${value.toFixed(value < 10 ? 2 : 1)} ${units[i]}`;
  }
}
