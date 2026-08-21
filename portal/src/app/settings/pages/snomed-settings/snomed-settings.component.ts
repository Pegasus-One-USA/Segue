import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { ToastService } from '../../../services/toast.service';
import { SnomedSettingsService, SnomedImportHistoryEntry } from '../../services/snomed-settings.service';
import { TerminologyImportHistoryComponent } from '../../components/terminology-import-history/terminology-import-history.component';
import { TerminologySchedulerCardComponent } from '../../components/terminology-scheduler-card/terminology-scheduler-card.component';
import { TerminologyHistoryPoller } from '../../utils/terminology-history-poller';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

@Component({
  selector: 'app-snomed-settings',
  standalone: true,
  imports: [ReactiveFormsModule, TerminologyImportHistoryComponent, TerminologySchedulerCardComponent],
  templateUrl: './snomed-settings.component.html',
  styleUrl: './snomed-settings.component.scss',
})
export class SnomedSettingsComponent implements OnInit, OnDestroy {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(SnomedSettingsService);
  private readonly toast = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  /** This route allows snomedct.view OR snomedct.write to enter — a view-only role must still see
   *  current settings/history, but Save/Synchronize/Import need the write check this flag provides. */
  protected readonly canWrite = this.permissions.hasPermission('snomedct.write');

  protected readonly loadingConfig = signal(true);
  protected readonly saving = signal(false);
  protected readonly syncing = signal(false);
  protected readonly apiKeySet = signal(false);
  protected readonly historyLoading = signal(true);
  protected readonly history = signal<SnomedImportHistoryEntry[]>([]);

  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    apiKey: [''],
    schedulerEnabled: [false],
    executionTime: ['03:00'],
    retryCount: [3],
    retryIntervalSeconds: [60],
  });

  private readonly poller = new TerminologyHistoryPoller(
    () => this.service.getHistory(),
    this.history,
    this.historyLoading,
    () => this.toast.error('Failed to load SNOMED CT import history'),
  );

  ngOnInit(): void {
    this.service.get().subscribe({
      next: (x) => {
        this.form.patchValue(x);
        this.apiKeySet.set(x.hasApiKeyConfigured);
        if (!this.canWrite) { this.form.disable({ emitEvent: false }); }
        this.loadingConfig.set(false);
      },
      error: () => {
        this.loadingConfig.set(false);
        this.toast.error('Failed to load SNOMED CT settings');
      },
    });
    this.poller.load();
  }

  ngOnDestroy(): void {
    this.poller.dispose();
  }

  protected save(): void {
    if (!this.actionGuard.ensure('snomedct.write', 'You do not have permission to modify SNOMED CT settings.')) return;
    if (this.form.invalid) return;
    this.saving.set(true);
    const v = this.form.getRawValue();
    this.service.update({ ...v, apiKey: v.apiKey.trim() || null }).subscribe({
      next: (x) => {
        this.apiKeySet.set(x.hasApiKeyConfigured);
        this.form.controls.apiKey.setValue('');
        this.form.markAsPristine();
        this.saving.set(false);
        this.toast.success('SNOMED CT settings saved');
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Failed to save SNOMED CT settings');
      },
    });
  }

  protected synchronize(): void {
    if (!this.actionGuard.ensure('snomedct.write', 'You do not have permission to synchronize SNOMED CT.')) return;
    if (this.form.dirty) {
      this.toast.error('Save settings before synchronizing');
      return;
    }
    this.syncing.set(true);
    this.service.synchronize().subscribe({
      next: () => {
        this.syncing.set(false);
        this.toast.success('SNOMED CT synchronization started in the background — check the history below for progress.');
        this.poller.load();
      },
      error: () => {
        this.syncing.set(false);
        this.toast.error('SNOMED CT synchronization failed to start');
      },
    });
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile.set(input.files?.[0] ?? null);
  }

  protected import(): void {
    const file = this.selectedFile();
    if (!file) return;
    if (!this.actionGuard.ensure('snomedct.write', 'You do not have permission to import SNOMED CT.')) return;
    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.service.importFile(file).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress.set(Math.round((100 * event.loaded) / event.total));
        } else if (event.type === HttpEventType.Response) {
          this.uploading.set(false);
          this.selectedFile.set(null);
          this.toast.success('SNOMED CT import started in the background — check the history below for progress.');
          this.poller.load();
        }
      },
      error: () => {
        this.uploading.set(false);
        this.toast.error('SNOMED CT upload failed');
      },
    });
  }
}
