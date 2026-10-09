import { Component, ElementRef, afterNextRender, computed, inject, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import {
  EHR_WRITE_OUTCOMES,
  EhrWriteReport,
  EhrWriteResourceCounts,
  ehrWriteReasons,
  ehrWriteReportToText,
  ehrWriteScopeLabel,
  ehrWriteTotals,
} from '../../models/ehr-write-report';

export interface EhrWriteReportDialogData {
  report: EhrWriteReport;
  /** When the destination node finished, for the subtitle. */
  writtenAt: string | null;
}

/** The EHR write-back report for one destination node run: what a dry run would have written, or what a live
 *  run wrote, per resource type, with the reason behind every record that was not sent. Counts and reason codes
 *  only — the report never carries resource content. */
@Component({
  selector: 'app-ehr-write-report-dialog',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './ehr-write-report-dialog.component.html',
  styleUrls: ['./ehr-write-report-dialog.component.scss'],
})
export class EhrWriteReportDialogComponent {
  readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as EhrWriteReportDialogData;
  private readonly toast = inject(ToastService);
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');

  constructor() {
    // The dialog shell does not move focus, so the dialog does: keyboard users land inside it, on Close.
    afterNextRender(() => this.closeButton()?.nativeElement.focus());
  }

  readonly report = this.data.report;
  readonly outcomes = EHR_WRITE_OUTCOMES;
  readonly totals = computed(() => ehrWriteTotals(this.report));
  readonly scope = ehrWriteScopeLabel(this.report.ScopeStatus);
  readonly vendor = this.report.TargetVendor || 'the EHR';

  readonly title = this.report.DryRun ? 'Dry-run report' : 'Write-back report';

  /** What this run did, so the reader knows how to read the numbers below. A live run is "live" as soon as one
   *  selected type can be sent; types that are only counted are called out, as are refusals and unknown outcomes,
   *  rather than letting a partly sent run read as fully written. */
  readonly headline = computed(() => {
    const totals = this.totals();
    if (this.report.DryRun) {
      return `Dry run: nothing was sent to ${this.vendor}. ${plural(totals.WouldWrite, 'record')} would be written by a live run.`;
    }

    const parts = [`Live run: ${plural(totals.Written, 'record')} written to ${this.vendor}.`];
    if (totals.WouldWrite > 0) {
      // Counted rather than sent: a dry-run-only type, or on runs recorded before the release setting was removed, an
      // unreleased one. The per-type reasons below say which, so the headline stays neutral.
      parts.push(`${plural(totals.WouldWrite, 'record')} only counted, not sent (see the reasons below).`);
    }
    if (totals.Rejected > 0) parts.push(`${plural(totals.Rejected, 'record')} refused.`);
    if (totals.Unknown > 0) {
      parts.push(`${plural(totals.Unknown, 'record')} with an unknown result: check EHR Write-Back Review before running again.`);
    }
    return parts.join(' ');
  });

  /** A live run that left records unsent, refused or unknown is not shown with the all-clear styling. */
  readonly headlineNeedsAttention = computed(() => {
    const totals = this.totals();
    return !this.report.DryRun && (totals.WouldWrite > 0 || totals.Rejected > 0 || totals.Unknown > 0);
  });

  reasonsFor(resource: EhrWriteResourceCounts) {
    return ehrWriteReasons(resource);
  }

  valueOf(resource: EhrWriteResourceCounts, key: (typeof EHR_WRITE_OUTCOMES)[number]['key']): number {
    return resource[key];
  }

  writtenAtText(): string | null {
    return this.data.writtenAt ? new Date(this.data.writtenAt).toLocaleString() : null;
  }

  copy(): void {
    navigator.clipboard.writeText(ehrWriteReportToText(this.report)).then(
      () => this.toast.show('Copied', 'Report copied to clipboard.'),
      () => this.toast.show('Copy failed', 'Select the text manually.'),
    );
  }
}

function plural(n: number, noun: string): string {
  return `${n.toLocaleString()} ${noun}${n === 1 ? '' : 's'}`;
}
