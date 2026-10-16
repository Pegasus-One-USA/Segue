import { Component, computed, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CdsTestResult } from '../../models/cds.models';
import { cdsOutcomeLabel, cdsOutcomeTone } from '../../models/cds-labels';
import { CdsCardPreviewComponent } from '../cds-card-preview/cds-card-preview.component';
import { CdsSystemActionsComponent } from '../cds-system-actions/cds-system-actions.component';

/**
 * The outcome of a test run: status line, any messages, the cards as an EHR would show them, any order updates
 * (systemActions), and the raw JSON.
 */
@Component({
  selector: 'app-cds-test-result',
  standalone: true,
  imports: [MatIconModule, CdsCardPreviewComponent, CdsSystemActionsComponent],
  templateUrl: './cds-test-result.component.html',
  styleUrl: './cds-test-result.component.scss',
})
export class CdsTestResultComponent {
  readonly result = input.required<CdsTestResult>();

  readonly showRaw = signal(false);

  readonly cards = computed(() => this.result().response?.cards ?? []);
  /** Coverage requirements only: order updates the EHR applies by itself. */
  readonly systemActions = computed(() => this.result().response?.systemActions ?? []);
  /** One-click fixes offered across the cards. */
  readonly fixCount = computed(() => this.cards().reduce((n, c) => n + (c.suggestions?.length ?? 0), 0));
  readonly outcomeLabel = computed(() => cdsOutcomeLabel(this.result().outcome));
  readonly tone = computed(() => cdsOutcomeTone(this.result().outcome));

  readonly rawResponse = computed(() => JSON.stringify(this.result().response, null, 2));
  readonly rawSettings = computed(() => JSON.stringify(this.result().resolvedSettings, null, 2));
}
