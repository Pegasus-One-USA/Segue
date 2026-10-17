import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/** The write options every EHR has: how many writes a run may make, how notes are filed, and whether a patient the
 *  EHR has no match for is created. */
@Component({
  selector: 'app-ehr-write-general-options',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-grid-2">
      <div class="dw-field">
        <label class="dw-label" for="dw-ewb-max">Max writes per run</label>
        <input id="dw-ewb-max" class="dw-input" [formControl]="maxWrites()" inputmode="numeric" placeholder="500" />
        @if (maxWrites().invalid) {
          <span class="dw-error">A whole number from 1 to 10000.</span>
        }
      </div>

      <div class="dw-field">
        <label class="dw-label" for="dw-ewb-docstatus">Clinical notes are filed as</label>
        <div class="dw-select-wrap">
          <select id="dw-ewb-docstatus" class="dw-select" [formControl]="noteDocStatus()">
            <option value="preliminary">Preliminary (a clinician reviews and signs)</option>
            <option value="final">Final (signed under the integration user)</option>
          </select>
        </div>
      </div>

      <div class="dw-field dw-field--full">
        <label class="dw-label">
          <input type="checkbox" [formControl]="createPatient()" />
          Create the patient when the EHR has no match
        </label>
        <span class="dw-hint">Only used when Patient.$match finds no one. An uncertain match is never written; it is
          left for review. A created patient's records are filed in the same run.</span>
      </div>
    </div>
  `,
})
export class EhrWriteGeneralOptionsComponent {
  readonly maxWrites = input.required<FormControl<string | null>>();
  readonly noteDocStatus = input.required<FormControl<string | null>>();
  readonly createPatient = input.required<FormControl<boolean | null>>();
}
