import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { EhrRunMode } from './ehr-write-back.model';

/** athenahealth's own write options: the provider results and notes are filed under, and the department new
 *  patients are registered in (the connection's, unless this destination sets its own). */
@Component({
  selector: 'app-ehr-athena-write-options',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-grid-2">
      <div class="dw-field" [class.dw-field--error]="providerId().invalid">
        <label class="dw-label" for="dw-ewb-provider">Provider id (athena)</label>
        <input id="dw-ewb-provider" class="dw-input" [formControl]="providerId()" placeholder="e.g. 71" />
        <span class="dw-hint">Optional: the provider athena files lab results and notes under.</span>
      </div>

      <div class="dw-field" [class.dw-field--error]="departmentId().invalid">
        <label class="dw-label" for="dw-ewb-department">Department id (athena)</label>
        <input id="dw-ewb-department" class="dw-input" [formControl]="departmentId()"
               [placeholder]="connectionDepartmentId() ? 'Connection default: ' + connectionDepartmentId() : 'e.g. 1'" />
        @if (connectionDepartmentId()) {
          <span class="dw-hint">Leave empty to use the connection's department ({{ connectionDepartmentId() }}), where
            new patients are registered. A value here overrides it for this destination only.</span>
        } @else {
          <span class="dw-hint">Where new patients are registered. Leave empty to write each chart in the patient's
            own primary department (new patients are then rejected).</span>
        }
        @if (runMode() === 'test') {
          <span class="dw-hint" data-testid="ewb-athena-test-hint">A test run uses the test server's settings, so set
            the department here if new patients should be registered.</span>
        }
      </div>
    </div>
  `,
})
export class EhrAthenaWriteOptionsComponent {
  readonly providerId = input.required<FormControl<string | null>>();
  readonly departmentId = input.required<FormControl<string | null>>();
  /** The chosen connection's own default department, if it has one. */
  readonly connectionDepartmentId = input<string | null>(null);
  readonly runMode = input<EhrRunMode | null>('dryRun');
}
