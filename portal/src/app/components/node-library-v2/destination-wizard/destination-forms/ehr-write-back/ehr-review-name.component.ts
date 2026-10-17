import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/** Review: the destination's name, for every destination type (Step 1 is only the connection). Left empty, it takes
 *  the chosen connection's name (shown as the placeholder). */
@Component({
  selector: 'app-ehr-review-name',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-field">
      <label class="dw-label" for="dw-ewb-review-name">Destination name</label>
      <input id="dw-ewb-review-name" class="dw-input" data-testid="ewb-review-name" [formControl]="control()"
        [placeholder]="placeholder()" maxlength="200" />
    </div>
  `,
})
export class EhrReviewNameComponent {
  readonly control = input.required<FormControl<string | null>>();
  /** The name used when the field is left empty: the chosen connection's. */
  readonly placeholder = input<string>('');
}
