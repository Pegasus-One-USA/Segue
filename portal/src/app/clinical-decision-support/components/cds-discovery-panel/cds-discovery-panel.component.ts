import { Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CdsDiscoveryInfo } from '../../models/cds.models';
import { cdsHookLabel } from '../../models/cds-labels';
import { CdsCopyButtonComponent } from '../cds-copy-button/cds-copy-button.component';

/** The address an EHR admin registers (discovery URL), each service's own URL, and short setup steps. */
@Component({
  selector: 'app-cds-discovery-panel',
  standalone: true,
  imports: [MatIconModule, CdsCopyButtonComponent],
  templateUrl: './cds-discovery-panel.component.html',
  styleUrl: './cds-discovery-panel.component.scss',
})
export class CdsDiscoveryPanelComponent {
  readonly info = input.required<CdsDiscoveryInfo>();

  readonly hookLabel = cdsHookLabel;
}
