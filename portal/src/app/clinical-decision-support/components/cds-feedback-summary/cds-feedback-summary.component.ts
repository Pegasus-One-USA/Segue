import { Component, computed, input } from '@angular/core';
import { CdsCallLog, cdsReasonCounts } from '../../models/cds.models';

/**
 * A count, over the calls on screen, of fixes accepted and of each dismiss reason code clinicians picked. It
 * shows which cards get overridden and why, so the rules can be tuned. Shows nothing when there is no feedback.
 */
@Component({
  selector: 'app-cds-feedback-summary',
  standalone: true,
  imports: [],
  templateUrl: './cds-feedback-summary.component.html',
  styleUrl: './cds-feedback-summary.component.scss',
})
export class CdsFeedbackSummaryComponent {
  readonly calls = input.required<CdsCallLog[]>();

  readonly reasonCounts = computed(() => {
    const counts = new Map<string, number>();
    for (const call of this.calls()) {
      for (const { code, count } of cdsReasonCounts(call)) counts.set(code, (counts.get(code) ?? 0) + count);
    }
    return [...counts.entries()]
      .map(([code, count]) => ({ code, count }))
      .sort((a, b) => b.count - a.count || a.code.localeCompare(b.code));
  });

  readonly fixesOffered = computed(() => this.calls().reduce((n, c) => n + (c.suggestionsOffered ?? 0), 0));
  readonly fixesAccepted = computed(() => this.calls().reduce((n, c) => n + (c.suggestionsAccepted ?? 0), 0));

  readonly hasAny = computed(() => this.reasonCounts().length > 0 || this.fixesOffered() > 0 || this.fixesAccepted() > 0);
}
