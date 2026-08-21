import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../../auth/store/auth.store';

interface TerminologySection {
  label: string;
  route: string;
  icon: string;
  /** True for a vocabulary whose auto-poll/import screen is blocked on something external (e.g. CPT's AMA
   * license) — rendered as a disabled, non-navigable tab instead of a normal link. */
  locked?: boolean;
  /** Each terminology system with real backend RBAC coverage has its own independent View/Write pair —
   *  see settings.routes.ts's per-system guards below this shell. Omitted for a system with no
   *  PermissionGroupCode yet (icd-10-pcs, hcpcs, ndc, cvx, ucum, cpt) — those stay reachable by anyone
   *  who can reach this shell at all, until real permission coverage is added for them. */
  permissions?: string[];
}

const TERMINOLOGY_SECTIONS: TerminologySection[] = [
  { label: 'LOINC', route: 'loinc', icon: 'biotech', permissions: ['loinc.view', 'loinc.write'] },
  { label: 'SNOMED CT', route: 'snomed-ct', icon: 'medical_information', permissions: ['snomedct.view', 'snomedct.write'] },
  { label: 'RxNorm', route: 'rxnorm', icon: 'medication', permissions: ['rxnorm.view', 'rxnorm.write'] },
  { label: 'ICD-10-CM', route: 'icd-10', icon: 'local_hospital', permissions: ['icd10.view', 'icd10.write'] },
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
  private readonly store = inject(AuthStore);

  // Same visibility rule as the parent shells — a role holding only e.g. loinc.view must see just
  // the LOINC tab here, not all four (each system is independently permission-controlled). A section
  // with no `permissions` at all (icd-10-pcs, hcpcs, ndc, cvx, ucum, cpt — no PermissionGroupCode
  // exists for them yet) is always shown, since there's no code to check it against.
  readonly sections = computed<TerminologySection[]>(() =>
    TERMINOLOGY_SECTIONS.filter(section =>
      !section.permissions?.length || this.store.isAdmin() || section.permissions.some(p => this.store.hasPermission(p))
    )
  );
}
