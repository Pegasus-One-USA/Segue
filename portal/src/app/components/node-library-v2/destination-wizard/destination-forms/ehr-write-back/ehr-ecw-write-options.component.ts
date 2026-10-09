import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { EhrMissingOptIn } from './ehr-write-back.model';

/** eClinicalWorks' own write options: the note author it requires, and the telephone encounter its medical and
 *  surgical history is filed on. */
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

      @if (offersHolderEncounter()) {
        <div class="dw-field dw-field--full">
          <label class="dw-label">
            <input type="checkbox" [formControl]="holderEncounter()" />
            File medical and surgical history on a new telephone encounter
          </label>
          <span class="dw-hint">eClinicalWorks takes history items only on an open telephone encounter. When on, one
            is created per patient per run, and only when a history item is actually sent. Off, history is skipped.</span>
          @if (holderTypes().length) {
            <span class="dw-hint dw-hint--warn" data-testid="ewb-holder-needed">Turn this on to write
              {{ holderTypes().join(', ') }}, or remove {{ holderTypes().length === 1 ? 'it' : 'them' }} under Resource types.</span>
          }
        </div>
      }
    </div>
  `,
})
export class EhrEcwWriteOptionsComponent {
  readonly providerId = input.required<FormControl<string | null>>();
  readonly holderEncounter = input.required<FormControl<boolean | null>>();
  readonly offersHolderEncounter = input<boolean>(false);
  readonly missing = input<EhrMissingOptIn[]>([]);

  readonly holderTypes = computed(() => this.missing().filter(m => m.holderEncounter).map(m => m.resourceType));
}
