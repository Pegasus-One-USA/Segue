import { Component, input, inject } from '@angular/core';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { WizardService } from '../../../../services/wizard.service';
import { ToastService } from '../../../../services/toast.service';
import { WIZARD_RESOURCES } from '../../models/epic-config.model';

@Component({
  selector: 'app-epic-step-data',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './epic-step-data.component.html',
  styleUrl: './epic-step-data.component.scss',
})
export class EpicStepDataComponent {
  readonly showAdvanced = input(false);

  protected readonly wiz   = inject(WizardService);
  private  readonly toast  = inject(ToastService);
  private  readonly fb     = inject(FormBuilder);

  /** Resource Type: Auto — use the resource types discovered from the source's /metadata; fall back to the static list. */
  get allResources(): string[] {
    const discovered = this.wiz.discoveredResourceTypes();
    return discovered.length ? discovered : WIZARD_RESOURCES;
  }

  /** True when the list above came from live discovery (lets the template show an "auto-detected" hint). */
  get resourcesAreAuto(): boolean {
    return this.wiz.discoveredResourceTypes().length > 0;
  }

  constructor() {
    // Trusted issuer is mandatory for EHR launch server-side; keep the wizard's value in sync with this field so
    // create-on-save (build) receives it. Seed the field from the wizard when returning to this step.
    const seeded = this.wiz.trustedIssuers().trim();
    if (seeded) this.form.controls.trustedIss.setValue(seeded);
    else this.wiz.trustedIssuers.set(this.form.controls.trustedIss.value);
    this.form.controls.trustedIss.valueChanges.subscribe(v => this.wiz.trustedIssuers.set((v ?? '').trim()));
  }

  readonly form = this.fb.nonNullable.group({
    trustedIss:         ['https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4'],
    clinicianRequired:  ['required'],
    idTokenValidation:  ['full'],
    fhirUserResolution: ['resolve'],
    patientContext:     ['required'],
    encounterContext:   ['optional'],
    locationContext:    ['optional'],
    appointmentContext: ['optional'],
    fhirContext:        ['accept'],
    needPatientBanner:  ['honor'],
    smartStyle:         ['apply'],
    tenantIntent:       ['capture'],
    framePolicy:        ['allow-epic'],
    scopeVersion:       ['v2'],
    baseScopes:         ['launch openid fhirUser online_access'],
  });

  isResourceChecked(r: string): boolean {
    return this.wiz.resources().includes(r);
  }

  toggleRes(r: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.wiz.toggleResource(r, checked);
  }

  validate(): boolean {
    if (this.wiz.resources().length === 0) {
      this.toast.show('Select resources', 'At least one FHIR resource must be selected.');
      return false;
    }
    return true;
  }
}
