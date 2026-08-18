import { Component, input } from '@angular/core';
import { DatePipe } from '@angular/common';

/** Structurally shared by every vocabulary's history-entry DTO (Loinc/RxNorm/Snomed/Icd10/...ImportHistoryEntry). */
export interface TerminologyImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

/**
 * Shared "Import History" card — extracted from the LOINC settings page so RxNorm/SNOMED/NDC/etc. don't each
 * clone the same table/status-badge markup. Presentational only: fetching and polling stay in the owning page.
 */
@Component({
  selector: 'app-terminology-import-history',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './terminology-import-history.component.html',
  styleUrl: './terminology-import-history.component.scss',
})
export class TerminologyImportHistoryComponent {
  readonly title = input('Import History');
  readonly subtitle = input('The most recent synchronization runs.');
  readonly emptyMessage = input('No release has been imported yet.');
  readonly countColumnLabel = input('Concepts');
  readonly history = input<TerminologyImportHistoryEntry[]>([]);
  readonly loading = input(false);
}
