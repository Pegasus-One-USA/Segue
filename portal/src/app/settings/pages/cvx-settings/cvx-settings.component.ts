import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { ToastService } from '../../../services/toast.service';
import { CvxSettingsService, CvxImportHistoryEntry } from '../../services/cvx-settings.service';
import { TerminologyImportHistoryComponent } from '../../components/terminology-import-history/terminology-import-history.component';
import { TerminologyHistoryPoller } from '../../utils/terminology-history-poller';

@Component({
  selector: 'app-cvx-settings',
  standalone: true,
  imports: [TerminologyImportHistoryComponent],
  templateUrl: './cvx-settings.component.html',
  styleUrl: './cvx-settings.component.scss',
})
export class CvxSettingsComponent implements OnInit, OnDestroy {
  private readonly service = inject(CvxSettingsService);
  private readonly toast = inject(ToastService);

  protected readonly historyLoading = signal(true);
  protected readonly history = signal<CvxImportHistoryEntry[]>([]);

  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);

  private readonly poller = new TerminologyHistoryPoller(
    () => this.service.getHistory(),
    this.history,
    this.historyLoading,
    () => this.toast.error('Failed to load CVX import history'),
  );

  ngOnInit(): void {
    this.poller.load();
  }

  ngOnDestroy(): void {
    this.poller.dispose();
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
          this.toast.success('CVX import started in the background — check the history below for progress.');
          this.poller.load();
        }
      },
      error: () => {
        this.uploading.set(false);
        this.toast.error('CVX upload failed');
      },
    });
  }
}
