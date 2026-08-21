import { Component } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * CPT is blocked on a paid AMA license/royalty agreement before any API access exists (AMA's "CPT Zip API" is
 * technically comparable to LOINC's Download API, but there's nothing to configure until that agreement is in
 * place) — so this is a static locked screen, not a form. No backend calls, no config.
 */
@Component({
  selector: 'app-cpt-settings',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cpt-settings.component.html',
  styleUrl: './cpt-settings.component.scss',
})
export class CptSettingsComponent {}
