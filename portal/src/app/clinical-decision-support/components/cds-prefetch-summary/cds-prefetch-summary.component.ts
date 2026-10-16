import { Component, computed, input } from '@angular/core';

/** Read-only list of the data a service asks the EHR to send with each call (its prefetch templates). */
@Component({
  selector: 'app-cds-prefetch-summary',
  standalone: true,
  templateUrl: './cds-prefetch-summary.component.html',
  styleUrl: './cds-prefetch-summary.component.scss',
})
export class CdsPrefetchSummaryComponent {
  readonly prefetch = input.required<Record<string, string>>();

  readonly entries = computed(() =>
    Object.entries(this.prefetch()).map(([key, query]) => ({ key, query }))
  );
}
