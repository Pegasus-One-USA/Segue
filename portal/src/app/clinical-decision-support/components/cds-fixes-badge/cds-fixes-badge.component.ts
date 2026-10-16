import { Component, input } from '@angular/core';

/** "Changes EHR drafts" on a service whose cards offer one-click fixes. Shows nothing when fixes are off. */
@Component({
  selector: 'app-cds-fixes-badge',
  standalone: true,
  imports: [],
  templateUrl: './cds-fixes-badge.component.html',
  styleUrl: './cds-fixes-badge.component.scss',
})
export class CdsFixesBadgeComponent {
  readonly enabled = input(false);
}
