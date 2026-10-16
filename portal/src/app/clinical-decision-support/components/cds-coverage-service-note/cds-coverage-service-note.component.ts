import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

/**
 * Notes on the service page for a coverage requirements (CRD) service. "settings": the payers and rules live on the
 * Coverage (CRD) tab. "fixes": one-click fixes do not apply; Segue updates the order itself instead.
 */
@Component({
  selector: 'app-cds-coverage-service-note',
  standalone: true,
  imports: [RouterLink, MatIconModule],
  templateUrl: './cds-coverage-service-note.component.html',
  styleUrl: './cds-coverage-service-note.component.scss',
})
export class CdsCoverageServiceNoteComponent {
  readonly part = input.required<'settings' | 'fixes'>();
}
