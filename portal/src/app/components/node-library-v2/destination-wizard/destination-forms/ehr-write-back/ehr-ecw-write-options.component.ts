import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/** eClinicalWorks' own write options: the note author it requires. The telephone encounter its medical and surgical
 *  history is filed on is turned on by ticking those kinds under Resource types, so it has no checkbox here. */
@Component({
  selector: 'app-ehr-ecw-write-options',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../destination-wizard.component.scss'],
  styles: [':host { display: block; }'],
  template: `
    <div class="dw-grid-2">
      <div class="dw-field" [class.dw-field--error]="providerId().invalid">
        <label class="dw-label" for="dw-ewb-provider">Note author (eCW practitioner id)</label>
        <input id="dw-ewb-provider" class="dw-input" [formControl]="providerId()" placeholder="e.g. 71" />
        <span class="dw-hint">eClinicalWorks files a note only with an author. Without one, notes are rejected.</span>
      </div>
    </div>
  `,
})
export class EhrEcwWriteOptionsComponent {
  readonly providerId = input.required<FormControl<string | null>>();
}
