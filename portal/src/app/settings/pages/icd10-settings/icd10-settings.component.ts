import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { Icd10SettingsService, Icd10ImportHistoryEntry } from '../../services/icd10-settings.service';
import { ReleaseFreshness } from '../../services/icd10pcs-settings.service';
import { TerminologyFreshnessCardComponent } from '../../components/terminology-freshness-card/terminology-freshness-card.component';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

const POLL_INTERVAL_MS = 3000;

@Component({
  selector: 'app-icd10-settings',
  standalone: true,
  imports: [DatePipe, TerminologyFreshnessCardComponent],
  templateUrl: './icd10-settings.component.html',
  styleUrl: './icd10-settings.component.scss',
})
export class Icd10SettingsComponent implements OnInit, OnDestroy {
  private readonly service = inject(Icd10SettingsService);
  private readonly toast = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private pollTimer?: ReturnType<typeof setTimeout>;

  /** This route allows icd10.view OR icd10.write to enter — a view-only role must still see current
   *  freshness/history, but Check/Download/Import need the write check this flag provides. */
  protected readonly canWrite = this.permissions.hasPermission('icd10.write');

  protected readonly loading = signal(true);
  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly history = signal<Icd10ImportHistoryEntry[]>([]);

  protected readonly checking = signal(false);
  protected readonly downloading = signal(false);
  protected readonly freshness = signal<ReleaseFreshness | null>(null);

  ngOnInit(): void {
    this.service.getFreshness().subscribe({ next: (f) => this.freshness.set(f) });
    this.loadHistory();
  }

  protected checkForUpdates(): void {
    if (!this.actionGuard.ensure('icd10.write', 'You do not have permission to check for ICD-10-CM updates.')) return;
    this.checking.set(true);
    this.service.checkForUpdates().subscribe({
      next: (f) => {
        this.checking.set(false);
        this.freshness.set(f);
        this.toast.success(f.newerReleaseFound ? 'A newer ICD-10-CM release appears to be available.' : 'No newer ICD-10-CM release found.');
      },
      error: () => {
        this.checking.set(false);
        this.toast.error('Failed to check for ICD-10-CM updates');
      },
    });
  }

  protected downloadAndImport(): void {
    if (!this.actionGuard.ensure('icd10.write', 'You do not have permission to import ICD-10-CM.')) return;
    this.downloading.set(true);
    this.service.downloadAndImport().subscribe({
      next: () => {
        this.downloading.set(false);
        this.toast.success('ICD-10-CM download and import started in the background — check the history below for progress.');
        this.loadHistory();
      },
      error: () => {
        this.downloading.set(false);
        this.toast.error('ICD-10-CM download and import failed to start');
      },
    });
  }

  ngOnDestroy(): void {
    clearTimeout(this.pollTimer);
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile.set(input.files?.[0] ?? null);
  }

  protected import(): void {
    const file = this.selectedFile();
    if (!file) return;
    if (!this.actionGuard.ensure('icd10.write', 'You do not have permission to import ICD-10-CM.')) return;
    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.service.importFile(file).subscribe({
      next: event => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress.set(Math.round((100 * event.loaded) / event.total));
        } else if (event.type === HttpEventType.Response) {
          this.uploading.set(false);
          this.selectedFile.set(null);
          this.toast.success('ICD-10-CM import started in the background — check the history below for progress.');
          this.loadHistory();
        }
      },
      error: () => {
        this.uploading.set(false);
        this.toast.error('ICD-10-CM upload failed');
      },
    });
  }

  private loadHistory(): void {
    this.loading.set(true);
    this.service.getHistory().subscribe({
      next: entries => {
        this.history.set(entries);
        this.loading.set(false);
        clearTimeout(this.pollTimer);
        if (entries.some(e => e.status === 'Running')) this.pollTimer = setTimeout(() => this.loadHistory(), POLL_INTERVAL_MS);
      },
      error: () => { this.loading.set(false); this.toast.error('Failed to load ICD-10-CM import history'); },
    });
  }
}
