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

  readonly allResources = WIZARD_RESOURCES;

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
