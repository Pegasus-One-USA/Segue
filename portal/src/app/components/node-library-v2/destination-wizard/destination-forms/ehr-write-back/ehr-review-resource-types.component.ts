import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EhrReviewTypeLine } from '../../ehr-write-type-grid/ehr-write-kinds.model';

/** The Review step's resource types for an EHR write-back destination: each type with the kinds of record written,
 *  in plain words (e.g. "Observation: Vital signs, Lines, drains and airways"). */
@Component({
  selector: 'app-ehr-review-resource-types',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: [':host { display: block; } .ewrt-line { display: block; }'],
  template: `
    @for (line of lines(); track line.resourceType) {
      <span class="ewrt-line" data-testid="ewb-review-type">{{ line.resourceType }}{{ line.kinds.length ? ': ' + line.kinds.join(', ') : '' }}</span>
    } @empty {
      —
    }
  `,
})
export class EhrReviewResourceTypesComponent {
  readonly lines = input<EhrReviewTypeLine[]>([]);
}
