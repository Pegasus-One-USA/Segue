import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { RxNormSettingsService, RxNormImportHistoryEntry } from '../../services/rxnorm-settings.service';

const POLL_INTERVAL_MS = 3000;

@Component({
  selector: 'app-rxnorm-settings',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './rxnorm-settings.component.html',
  styleUrl: './rxnorm-settings.component.scss',
})
export class RxnormSettingsComponent implements OnInit, OnDestroy {
  private readonly service = inject(RxNormSettingsService);
  private readonly toast = inject(ToastService);
  private pollTimer?: ReturnType<typeof setTimeout>;

  protected readonly loading = signal(true);
  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly history = signal<RxNormImportHistoryEntry[]>([]);

  ngOnInit(): void {
    this.loadHistory();
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
    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.service.importFile(file).subscribe({
      next: event => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress.set(Math.round((100 * event.loaded) / event.total));
        } else if (event.type === HttpEventType.Response) {
          this.uploading.set(false);
          this.selectedFile.set(null);
          this.toast.success('RxNorm import started in the background — check the history below for progress.');
          this.loadHistory();
        }
      },
      error: () => {
        this.uploading.set(false);
        this.toast.error('RxNorm upload failed');
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
      error: () => { this.loading.set(false); this.toast.error('Failed to load RxNorm import history'); },
    });
  }
}
