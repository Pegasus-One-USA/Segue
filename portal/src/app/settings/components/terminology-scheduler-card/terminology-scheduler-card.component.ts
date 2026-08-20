import { Component, input, output } from '@angular/core';
import { ReactiveFormsModule, FormGroup } from '@angular/forms';

/**
 * Shared "Automatic Synchronization" card — extracted from the LOINC settings page's Synchronization section so
 * RxNorm/SNOMED/NDC/UCUM/etc. don't each clone the same toggle/frequency/retry markup. The consuming page owns its
 * own `FormGroup` (control names below must exist on it whenever the corresponding `show*` flag is on) and its own
 * `save`/`synchronize` handlers — this component is purely the field layout, parameterized per vocabulary:
 *
 * - `frequencyOptions`: e.g. LOINC offers ['Monthly', 'Weekly']; RxNorm/SNOMED have a fixed cadence and pass `[]`
 *   with `showFrequency` false; NDC offers ['Daily', 'Weekly'].
 * - `showRetryFields`/`showDownloadTimeout`: RxNorm/SNOMED/NDC/UCUM don't need every field LOINC does.
 *
 * Required controls on `form()`, gated by the matching `show*`/`frequencyOptions` input:
 * `schedulerEnabled` (always), `executionTime` (always), `frequency` (when `frequencyOptions().length > 0`),
 * `retryCount`/`retryIntervalSeconds` (when `showRetryFields()`), `downloadTimeoutSeconds` (when `showDownloadTimeout()`).
 */
@Component({
  selector: 'app-terminology-scheduler-card',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './terminology-scheduler-card.component.html',
  styleUrl: './terminology-scheduler-card.component.scss',
})
export class TerminologySchedulerCardComponent {
  readonly form = input.required<FormGroup>();
  readonly title = input('Automatic Synchronization');
  readonly subtitle = input('Schedule automatic release imports, or run one on demand below.');
  readonly frequencyOptions = input<string[]>([]);
  readonly showRetryFields = input(false);
  readonly showDownloadTimeout = input(false);
  readonly saving = input(false);
  readonly syncing = input(false);
  readonly saveDisabled = input(false);
  /** Additive to `syncing()` — the consuming page passes `!hasWritePermission` here for a
   *  view-only visitor of a route that (unlike most settings screens) allows `.view` OR `.write` to
   *  enter at all (see LOINC/SNOMED/RxNorm/ICD-10 settings.routes.ts entries). Defaults to false so
   *  pages that don't pass it (NDC, UCUM — no permission group of their own yet) are unaffected. */
  readonly syncDisabled = input(false);

  readonly save = output<void>();
  readonly synchronize = output<void>();
}
