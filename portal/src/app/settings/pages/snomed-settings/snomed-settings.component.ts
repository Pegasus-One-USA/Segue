import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { SnomedSettingsService, SnomedImportHistoryEntry } from '../../services/snomed-settings.service';

@Component({
  selector: 'app-snomed-settings',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './snomed-settings.component.html',
  styleUrl: './snomed-settings.component.scss',
})
export class SnomedSettingsComponent implements OnInit {
  private readonly service = inject(SnomedSettingsService);
  private readonly toast = inject(ToastService);

  protected readonly loading = signal(true);
  protected readonly importing = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly history = signal<SnomedImportHistoryEntry[]>([]);

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
          this.toast.success(`Imported ${event.body?.importedConceptCount ?? 0} SNOMED CT concepts (release ${event.body?.version ?? ''})`);
          this.loadHistory();
        }
      },
      error: () => {
        this.importing.set(false);
        this.toast.error('SNOMED CT import failed');
        this.loadHistory();
      },
    });
  }

  private loadHistory(): void {
    this.loading.set(true);
    this.service.getHistory().subscribe({
      next: entries => { this.history.set(entries); this.loading.set(false); },
      error: () => { this.loading.set(false); this.toast.error('Failed to load SNOMED CT import history'); },
    });
  }
}
