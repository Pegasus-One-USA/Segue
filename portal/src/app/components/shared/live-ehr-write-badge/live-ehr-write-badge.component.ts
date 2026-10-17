import { Component, computed, input } from '@angular/core';
import { ehrNamesOf } from '../../../services/live-ehr-write-targets.util';

/** "Writes to <EHR names>" on a workflow whose EHR Write-Back destinations write into an EHR for real. Shows nothing
 *  when every write-back is a test or dry run, or there is none. `vendors` are the EHR vendor codes (Epic, Healow…). */
@Component({
  selector: 'app-live-ehr-write-badge',
  standalone: true,
  imports: [],
  templateUrl: './live-ehr-write-badge.component.html',
  styleUrl: './live-ehr-write-badge.component.scss',
})
export class LiveEhrWriteBadgeComponent {
  readonly vendors = input<readonly string[] | null | undefined>([]);

  protected readonly ehr = computed(() => ehrNamesOf(this.vendors() ?? []));
}
