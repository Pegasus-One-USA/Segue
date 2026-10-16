import { Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { CdsReadiness, CdsReadinessStatus } from '../../models/cds.models';

const STATUS_ICON: Record<CdsReadinessStatus, string> = {
  ok: 'check_circle',
  warning: 'warning_amber',
  blocker: 'block',
};

const STATUS_LABEL: Record<CdsReadinessStatus, string> = {
  ok: 'Ready',
  warning: 'Review',
  blocker: 'Blocker',
};

/**
 * Go-live readiness on Connect your EHR: every check the API runs (https address, replay protection, trusted
 * issuers, services, clinical sign-off, licence, rate limit) with its status colour and how to fix it.
 */
@Component({
  selector: 'app-cds-readiness-panel',
  standalone: true,
  imports: [DatePipe, MatIconModule],
  templateUrl: './cds-readiness-panel.component.html',
  styleUrl: './cds-readiness-panel.component.scss',
})
export class CdsReadinessPanelComponent {
  /** Null when the report could not be loaded. */
  readonly readiness = input<CdsReadiness | null>(null);
  readonly loading = input(false);

  readonly refresh = output<void>();

  readonly blockers = computed(() => this.readiness()?.checks.filter(c => c.status === 'blocker').length ?? 0);
  readonly warnings = computed(() => this.readiness()?.checks.filter(c => c.status === 'warning').length ?? 0);

  /** One line for the whole report. */
  readonly headline = computed(() => {
    const blockers = this.blockers();
    const warnings = this.warnings();
    if (blockers > 0) {
      return `${blockers} ${blockers === 1 ? 'blocker' : 'blockers'} to fix before go-live`
        + (warnings > 0 ? `, and ${warnings} ${warnings === 1 ? 'item' : 'items'} to review` : '');
    }
    if (warnings > 0) return `No blockers. ${warnings} ${warnings === 1 ? 'item' : 'items'} to review before go-live`;
    return 'Ready to go live';
  });

  icon(status: CdsReadinessStatus): string {
    return STATUS_ICON[status] ?? 'help_outline';
  }

  statusLabel(status: CdsReadinessStatus): string {
    return STATUS_LABEL[status] ?? status;
  }
}
