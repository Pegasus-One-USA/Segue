import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/** Options: run as a dry run (check every record, send nothing). Offered while the EhrWriteBack:DryRunEnabled setting
 *  is on, and to a destination saved as a dry run, which keeps it ticked with a note while the setting is off. */
@Component({
  selector: 'app-ehr-dry-run-option',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-field dw-field--full">
      <label class="dw-label">
        <input type="checkbox" data-testid="ewb-dry-run" [formControl]="control()" />
        Dry run: check every record, send nothing
      </label>
      @if (settingOff() && control().value) {
        <span class="dw-hint" data-testid="ewb-dry-run-off-note">Dry run is turned off in System Settings. This
          destination stays a dry run until you untick it.</span>
      }
    </div>
  `,
})
export class EhrDryRunOptionComponent {
  readonly control = input.required<FormControl<boolean | null>>();
  /** The EhrWriteBack:DryRunEnabled setting is off (the destination was saved as a dry run). */
  readonly settingOff = input<boolean>(false);
}
