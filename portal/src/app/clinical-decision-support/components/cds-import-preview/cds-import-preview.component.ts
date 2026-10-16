import { Component, computed, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CdsCoverageImportAction, CdsCoverageImportResult } from '../../models/cds-coverage.models';
import { cdsImportActionLabel } from '../../models/cds-coverage-labels';

type RowFilter = 'all' | 'error' | 'changes';

/**
 * What a rule file would do (or did): counts of rules added, updated, unchanged and deleted, problems with the file
 * as a whole, and every row with its action and errors. Rows with errors can be shown on their own.
 */
@Component({
  selector: 'app-cds-import-preview',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-import-preview.component.html',
  styleUrl: './cds-import-preview.component.scss',
})
export class CdsImportPreviewComponent {
  readonly result = input.required<CdsCoverageImportResult>();

  readonly filter = signal<RowFilter>('all');
  readonly actionLabel = cdsImportActionLabel;

  readonly hasErrors = computed(() => this.result().errorCount > 0 || this.result().fileErrors.length > 0);

  readonly rows = computed(() => {
    const rows = this.result().rows;
    const filter = this.filter();
    if (filter === 'error') return rows.filter(r => r.action === 'error');
    if (filter === 'changes') return rows.filter(r => r.action !== 'unchanged');
    return rows;
  });

  tone(action: CdsCoverageImportAction | string): string {
    switch (action) {
      case 'add': return 'success';
      case 'update': return 'info';
      case 'delete': return 'warning';
      case 'error': return 'error';
      default: return '';
    }
  }

  setFilter(value: string): void {
    this.filter.set(value as RowFilter);
  }
}
