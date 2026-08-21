import { Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { SimpleStubFormEngine } from './simple-stub-form.helper';
import { WizardDestinationFormApi } from './destination-form-api';

/** Minimal stand-in form for the FhirRepository destination type — see SimpleStubFormEngine. */
@Component({
  selector: 'app-fhir-repository-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './simple-stub-destination-form.component.html',
})
export class FhirRepositoryDestinationFormComponent implements WizardDestinationFormApi {
  // Key is `fhirBaseUrl` (not `baseUrl`) so getFullConfig()/getMetadata() emit `dest_fhirBaseUrl` — the key
  // the whole pipeline expects for a FhirRepository destination (see workflow-build-assembler.service.ts's
  // isFhir branch, destination-connection-secret.util.ts's fhir metadata keys, and the wizard's
  // provisionDestinationConnection isFhir branch, all of which read `dest_fhirBaseUrl`).
  private readonly engine = new SimpleStubFormEngine(inject(FormBuilder), 'FHIR Repository Destination', [
    { key: 'fhirBaseUrl', label: 'FHIR base URL', placeholder: 'https://fhir.example.com/r4' },
  ]);
  readonly form = this.engine.form;
  readonly extraFields = this.engine.extraFields;

  isValid(): boolean { return this.engine.isValid(); }
  getRawValue(): Record<string, unknown> { return this.engine.getRawValue(); }
  getFullConfig(): Record<string, string> { return this.engine.getFullConfig(); }
  getMetadata() { return this.engine.getMetadata(); }
  patchFrom(fields: Record<string, string>, target?: string | null): void { this.engine.patchFrom(fields, target); }
  reset(): void { this.engine.reset(); }
}
