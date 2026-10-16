import { Component, computed, input } from '@angular/core';
import { CdsCallLog } from '../../models/cds.models';
import { cdsCoverageDecisionLabel } from '../../models/cds-coverage-labels';
import { CDS_COVERAGE_HANDLER_KEY } from '../../models/cds-coverage.models';

interface Fact {
  label: string;
  tip: string;
  tone?: 'warning';
}

/**
 * The coverage requirements (CRD) facts of one Activity row: the payer, each order's decision in plain words, the
 * relay (used, time, outcome), and how many orders were updated and coverage answers given. Shows nothing for a
 * call of another service. Codes and counts only, never patient data.
 */
@Component({
  selector: 'app-cds-call-coverage',
  standalone: true,
  imports: [],
  templateUrl: './cds-call-coverage.component.html',
  styleUrl: './cds-call-coverage.component.scss',
})
export class CdsCallCoverageComponent {
  readonly call = input.required<CdsCallLog>();

  /** True for a call (not feedback) to a coverage requirements service, or any row carrying CRD fields. */
  readonly isCoverage = computed(() => {
    const c = this.call();
    return (c.kind === 'Invoke' && c.handlerKey === CDS_COVERAGE_HANDLER_KEY)
      || !!c.payerName
      || !!c.coverageDecisions?.length
      || (c.relayUsed !== null && c.relayUsed !== undefined)
      || (c.coverageAssertionCount !== null && c.coverageAssertionCount !== undefined)
      || !!c.systemActionCount;
  });

  readonly decisions = computed(() => (this.call().coverageDecisions ?? []).map(d => cdsCoverageDecisionLabel(d)));

  readonly facts = computed<Fact[]>(() => {
    const c = this.call();
    const list: Fact[] = [];
    if (c.relayUsed) {
      const ms = c.relayMs !== null && c.relayMs !== undefined ? ` in ${c.relayMs} ms` : '';
      const ok = !c.relayOutcome || c.relayOutcome === 'ok';
      list.push({
        label: ok ? `Asked the payer${ms}` : `Payer: ${c.relayOutcome}${ms}`,
        tip: ok ? "The call was sent on to the payer's own service, which answered." : "The call was sent on to the payer's own service, which did not answer properly.",
        tone: ok ? undefined : 'warning',
      });
    } else if (c.relayUsed === false && c.relayOutcome && c.relayOutcome !== 'ok') {
      list.push({ label: `Relay: ${c.relayOutcome}`, tip: "Why the payer's own service was not used.", tone: 'warning' });
    }
    if (c.systemActionCount) {
      list.push({
        label: `${c.systemActionCount} ${c.systemActionCount === 1 ? 'order' : 'orders'} updated`,
        tip: 'Orders the EHR updated with the coverage information (systemActions).',
      });
    }
    if (c.coverageAssertionCount) {
      list.push({
        label: `${c.coverageAssertionCount} coverage ${c.coverageAssertionCount === 1 ? 'answer' : 'answers'}`,
        tip: 'Coverage information entries returned, each with its own coverage assertion id.',
      });
    }
    return list;
  });
}
