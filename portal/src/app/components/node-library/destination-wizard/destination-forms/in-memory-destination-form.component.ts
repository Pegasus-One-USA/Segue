import { Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { SimpleStubFormEngine } from './simple-stub-form.helper';
import { WizardDestinationFormApi } from './destination-form-api';

/** Minimal stand-in form for the InMemory destination type (a discard/test sink) — see
 *  SimpleStubFormEngine for the shared name/target field logic. */
@Component({
  selector: 'app-in-memory-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './simple-stub-destination-form.component.html',
})
export class InMemoryDestinationFormComponent implements WizardDestinationFormApi {
  private readonly engine = new SimpleStubFormEngine(inject(FormBuilder), 'In-Memory Sink (test)', []);
  readonly form = this.engine.form;
  readonly extraFields = this.engine.extraFields;

  isValid(): boolean { return this.engine.isValid(); }
  getRawValue(): Record<string, unknown> { return this.engine.getRawValue(); }
  getFullConfig(): Record<string, string> { return this.engine.getFullConfig(); }
  getMetadata() { return this.engine.getMetadata(); }
  patchFrom(fields: Record<string, string>, target?: string | null): void { this.engine.patchFrom(fields, target); }
  reset(): void { this.engine.reset(); }
}
