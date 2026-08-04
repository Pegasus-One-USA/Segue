import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

interface TerminologySection {
  label: string;
  route: string;
  icon: string;
}

// Every SuperAdmin who can reach this shell (see settings.routes.ts's superAdminGuard) can see all
// four sections — same flat, unguarded layout as the parent SYSTEM_SETTINGS_SECTIONS list.
const TERMINOLOGY_SECTIONS: TerminologySection[] = [
  { label: 'LOINC', route: 'loinc', icon: 'biotech' },
  { label: 'SNOMED CT', route: 'snomed-ct', icon: 'medical_information' },
  { label: 'RxNorm', route: 'rxnorm', icon: 'medication' },
  { label: 'ICD-10', route: 'icd-10', icon: 'local_hospital' },
];

@Component({
  selector: 'app-terminology-configurations-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './terminology-configurations-shell.component.html',
  styleUrl: './terminology-configurations-shell.component.scss',
})
export class TerminologyConfigurationsShellComponent {
  readonly sections = TERMINOLOGY_SECTIONS;
}
