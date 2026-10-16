import { Component, input } from '@angular/core';
import { DatePipe } from '@angular/common';

/**
 * A service's clinical review state: "Reviewed by X on <date>" when a clinician's sign-off is on record, else
 * "Not clinically reviewed". Advisory only; it never stops a service from being switched on.
 */
@Component({
  selector: 'app-cds-sign-off-badge',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './cds-sign-off-badge.component.html',
  styleUrl: './cds-sign-off-badge.component.scss',
})
export class CdsSignOffBadgeComponent {
  readonly signedOffBy = input<string | null>(null);
  readonly signedOffAtUtc = input<string | null>(null);
}
