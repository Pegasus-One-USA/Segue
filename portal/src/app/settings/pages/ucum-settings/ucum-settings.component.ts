import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { ToastService } from '../../../services/toast.service';
import { UcumSettingsService, UcumImportHistoryEntry } from '../../services/ucum-settings.service';
import { TerminologyImportHistoryComponent } from '../../components/terminology-import-history/terminology-import-history.component';
import { TerminologySchedulerCardComponent } from '../../components/terminology-scheduler-card/terminology-scheduler-card.component';
import { TerminologyHistoryPoller } from '../../utils/terminology-history-poller';

@Component({
  selector: 'app-ucum-settings',
  standalone: true,
  imports: [ReactiveFormsModule, TerminologyImportHistoryComponent, TerminologySchedulerCardComponent],
  templateUrl: './ucum-settings.component.html',
  styleUrl: './ucum-settings.component.scss',
})
export class UcumSettingsComponent implements OnInit, OnDestroy {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(UcumSettingsService);
  private readonly toast = inject(ToastService);

  protected readonly loadingConfig = signal(true);
  protected readonly saving = signal(false);
  protected readonly syncing = signal(false);
  protected readonly historyLoading = signal(true);
  protected readonly history = signal<UcumImportHistoryEntry[]>([]);

  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    schedulerEnabled: [false],
    frequency: ['Weekly'],
    executionTime: ['05:00'],
  });

  private readonly poller = new TerminologyHistoryPoller(
    () => this.service.getHistory(),
    this.history,
    this.historyLoading,
    () => this.toast.error('Failed to load UCUM import history'),
  );

  ngOnInit(): void {
    this.service.get().subscribe({
      next: (x) => {
        this.form.patchValue(x);
        this.loadingConfig.set(false);
      },
      error: () => {
        this.loadingConfig.set(false);
        this.toast.error('Failed to load UCUM settings');
      },
    });
    this.poller.load();
  }

  ngOnDestroy(): void {
    this.poller.dispose();
  }

  protected save(): void {
    if (this.form.invalid) return;
    this.saving.set(true);
    this.service.update(this.form.getRawValue()).subscribe({
      next: () => {
        this.form.markAsPristine();
        this.saving.set(false);
        this.toast.success('UCUM settings saved');
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Failed to save UCUM settings');
      },
    });
  }

  protected synchronize(): void {
    if (this.form.dirty) {
      this.toast.error('Save settings before synchronizing');
      return;
    }
    this.syncing.set(true);
    this.service.synchronize().subscribe({
      next: () => {
        this.syncing.set(false);
        this.toast.success('UCUM synchronization started in the background — check the history below for progress.');
        this.poller.load();
      },
      error: () => {
        this.syncing.set(false);
        this.toast.error('UCUM synchronization failed to start');
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
          this.toast.success('UCUM import started in the background — check the history below for progress.');
          this.poller.load();
        }
      },
      error: () => {
        this.uploading.set(false);
        this.toast.error('UCUM upload failed');
      },
    });
  }
}
