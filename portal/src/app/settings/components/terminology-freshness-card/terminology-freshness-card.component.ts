import { Component, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';

export interface TerminologyReleaseFreshness {
  newerReleaseFound: boolean;
  latestKnownFileName: string | null;
  checkedOnUtc: string;
}

/**
 * Shared "Release Freshness" card for vocabularies with no version-check API (ICD-10-CM/PCS, HCPCS) — a lighter
 * touch than app-terminology-scheduler-card: no schedule, just a manual "Check for Updates" scrape and a
 * last-checked timestamp. Reused instead of cloning the same three lines across three settings pages.
 *
 * Once a check has identified a file, a "Download & Import" action becomes available — these three sources are
 * all free/public downloads, so there's no reason to make the user manually download-then-upload once the
 * checker already knows the exact file. Deliberately a separate button from "Check for Updates" (not automatic
 * on finding something new) so a button that reads as "just checking" never silently triggers a real import.
 */
@Component({
  selector: 'app-terminology-freshness-card',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './terminology-freshness-card.component.html',
  styleUrl: './terminology-freshness-card.component.scss',
})
export class TerminologyFreshnessCardComponent {
  readonly freshness = input<TerminologyReleaseFreshness | null>(null);
  readonly checking = input(false);
  readonly downloading = input(false);
  readonly checkForUpdates = output<void>();
  readonly downloadAndImport = output<void>();
}
