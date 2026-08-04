import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { Icd10SettingsService, Icd10ImportHistoryEntry } from '../../services/icd10-settings.service';

@Component({
  selector: 'app-icd10-settings',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './icd10-settings.component.html',
  styleUrl: './icd10-settings.component.scss',
})
export class Icd10SettingsComponent implements OnInit {
  private readonly service = inject(Icd10SettingsService);
  private readonly toast = inject(ToastService);

  protected readonly loading = signal(true);
  protected readonly importing = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly history = signal<Icd10ImportHistoryEntry[]>([]);

  ngOnInit(): void {
    this.loadHistory();
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile.set(input.files?.[0] ?? null);
  }

  protected import(): void {
    const file = this.selectedFile();
    if (!file) return;
    this.importing.set(true);
    this.uploadProgress.set(0);
    this.service.importFile(file).subscribe({
      next: event => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress.set(Math.round((100 * event.loaded) / event.total));
        } else if (event.type === HttpEventType.Response) {
          this.importing.set(false);
          this.selectedFile.set(null);
          this.toast.success(`Imported ${event.body?.importedCodeCount ?? 0} ICD-10-CM codes (release ${event.body?.version ?? ''})`);
          this.loadHistory();
        }
      },
      error: () => {
        this.importing.set(false);
        this.toast.error('ICD-10-CM import failed');
        this.loadHistory();
      },
    });
  }

  private loadHistory(): void {
    this.loading.set(true);
    this.service.getHistory().subscribe({
      next: entries => { this.history.set(entries); this.loading.set(false); },
      error: () => { this.loading.set(false); this.toast.error('Failed to load ICD-10-CM import history'); },
    });
  }
}
