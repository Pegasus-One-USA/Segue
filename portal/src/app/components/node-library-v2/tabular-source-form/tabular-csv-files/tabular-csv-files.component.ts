import { Component, inject, input, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { TabularSourceFile, TabularSourceService } from '../../../../services/tabular-source.service';

/**
 * Step 1 of a CSV-backed CSV / SQL Table source: upload the files its resource types read, and list them. Files are
 * stored encrypted by the API; only their names, row counts and headers come back.
 */
@Component({
  selector: 'app-tabular-csv-files',
  standalone: true,
  templateUrl: './tabular-csv-files.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularCsvFilesComponent {
  private readonly api = inject(TabularSourceService);

  readonly files = input<TabularSourceFile[]>([]);
  readonly uploaded = output<TabularSourceFile>();

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  onFileChosen(event: Event): void {
    const element = event.target as HTMLInputElement;
    const file = element.files?.[0];
    if (!file) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.upload(file).subscribe({
      next: result => {
        this.busy.set(false);
        element.value = '';
        this.uploaded.emit(result);
      },
      error: (e: HttpErrorResponse) => {
        this.busy.set(false);
        const body = e.error as { message?: string; detail?: string; error_description?: string } | null;
        this.error.set(body?.message || body?.detail || body?.error_description || 'The upload failed. Try again.');
      },
    });
  }
}
