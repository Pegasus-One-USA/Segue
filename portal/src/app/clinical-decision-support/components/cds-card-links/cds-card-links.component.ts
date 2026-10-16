import { Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CdsLink } from '../../models/cds.models';

/**
 * A card's link buttons. Only http(s) links are clickable. A SMART app link is marked as such: in the EHR it
 * launches inside the chart with the EHR's context, which a portal preview can't give it.
 */
@Component({
  selector: 'app-cds-card-links',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-card-links.component.html',
  styleUrl: './cds-card-links.component.scss',
})
export class CdsCardLinksComponent {
  readonly links = input.required<CdsLink[]>();

  isSafeUrl(url: string | undefined): boolean {
    return !!url && /^https?:\/\//i.test(url);
  }
}
