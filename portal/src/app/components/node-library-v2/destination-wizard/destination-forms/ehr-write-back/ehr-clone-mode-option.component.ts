import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/** QA only (offered while the clone-mode system setting is on): every patient is written as a new synthetic one. */
@Component({
  selector: 'app-ehr-clone-mode-option',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-field dw-field--full">
      <label class="dw-label">
        <input type="checkbox" [formControl]="control()" />
        Clone mode (QA only) — write each patient as a new synthetic test patient
      </label>
      <span class="dw-hint">Every source patient is created as a clone: "Zztest" before the family name, a shifted
        birth date, a synthetic SSN and no phone or email. Records are filed against the clone, never the
        original. Select Patient as well; notes and vitals are skipped because a clone has no encounters.</span>
    </div>
  `,
})
export class EhrCloneModeOptionComponent {
  readonly control = input.required<FormControl<boolean | null>>();
}
