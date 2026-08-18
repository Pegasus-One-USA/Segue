import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { ToastService } from '../../../services/toast.service';
import { Icd10PcsSettingsService, Icd10PcsImportHistoryEntry, ReleaseFreshness } from '../../services/icd10pcs-settings.service';
import { TerminologyImportHistoryComponent } from '../../components/terminology-import-history/terminology-import-history.component';
import { TerminologyFreshnessCardComponent } from '../../components/terminology-freshness-card/terminology-freshness-card.component';
import { TerminologyHistoryPoller } from '../../utils/terminology-history-poller';

@Component({
  selector: 'app-icd10pcs-settings',
  standalone: true,
  imports: [TerminologyImportHistoryComponent, TerminologyFreshnessCardComponent],
  templateUrl: './icd10pcs-settings.component.html',
  styleUrl: './icd10pcs-settings.component.scss',
})
export class Icd10PcsSettingsComponent implements OnInit, OnDestroy {
  private readonly service = inject(Icd10PcsSettingsService);
  private readonly toast = inject(ToastService);

  protected readonly checking = signal(false);
  protected readonly downloading = signal(false);
  protected readonly freshness = signal<ReleaseFreshness | null>(null);
  protected readonly historyLoading = signal(true);
  protected readonly history = signal<Icd10PcsImportHistoryEntry[]>([]);

  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);

  private readonly poller = new TerminologyHistoryPoller(
    () => this.service.getHistory(),
    this.history,
    this.historyLoading,
    () => this.toast.error('Failed to load ICD-10-PCS import history'),
  );

  ngOnInit(): void {
    this.service.getFreshness().subscribe({ next: (f) => this.freshness.set(f) });
    this.poller.load();
  }

  ngOnDestroy(): void {
    this.poller.dispose();
  }

  protected checkForUpdates(): void {
    this.checking.set(true);
    this.service.checkForUpdates().subscribe({
      next: (f) => {
        this.checking.set(false);
        this.freshness.set(f);
        this.toast.success(f.newerReleaseFound ? 'A newer ICD-10-PCS release appears to be available.' : 'No newer ICD-10-PCS release found.');
      },
      error: () => {
        this.checking.set(false);
        this.toast.error('Failed to check for ICD-10-PCS updates');
      },
    });
  }

  protected downloadAndImport(): void {
    this.downloading.set(true);
    this.service.downloadAndImport().subscribe({
      next: () => {
        this.downloading.set(false);
        this.toast.success('ICD-10-PCS download and import started in the background — check the history below for progress.');
        this.poller.load();
      },
      error: () => {
        this.downloading.set(false);
        this.toast.error('ICD-10-PCS download and import failed to start');
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
    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.service.importFile(file).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress.set(Math.round((100 * event.loaded) / event.total));
        } else if (event.type === HttpEventType.Response) {
          this.uploading.set(false);
          this.selectedFile.set(null);
          this.toast.success('ICD-10-PCS import started in the background — check the history below for progress.');
          this.poller.load();
        }
      },
      error: () => {
        this.uploading.set(false);
        this.toast.error('ICD-10-PCS upload failed');
      },
    });
  }
}
