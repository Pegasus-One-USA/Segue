import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

interface TerminologySection {
  label: string;
  route: string;
  icon: string;
  /** True for a vocabulary whose auto-poll/import screen is blocked on something external (e.g. CPT's AMA
   * license) — rendered as a disabled, non-navigable tab instead of a normal link. */
  locked?: boolean;
}

// Every SuperAdmin who can reach this shell (see settings.routes.ts's superAdminGuard) can see all
// sections — same flat, unguarded layout as the parent SYSTEM_SETTINGS_SECTIONS list.
const TERMINOLOGY_SECTIONS: TerminologySection[] = [
  { label: 'LOINC', route: 'loinc', icon: 'biotech' },
  { label: 'SNOMED CT', route: 'snomed-ct', icon: 'medical_information' },
  { label: 'RxNorm', route: 'rxnorm', icon: 'medication' },
  { label: 'ICD-10-CM', route: 'icd-10', icon: 'local_hospital' },
  { label: 'ICD-10-PCS', route: 'icd-10-pcs', icon: 'health_and_safety' },
  { label: 'HCPCS', route: 'hcpcs', icon: 'medical_services' },
  { label: 'NDC', route: 'ndc', icon: 'vaccines' },
  { label: 'CVX', route: 'cvx', icon: 'syringe' },
  { label: 'UCUM', route: 'ucum', icon: 'straighten' },
  { label: 'CPT', route: 'cpt', icon: 'lock', locked: true },
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
