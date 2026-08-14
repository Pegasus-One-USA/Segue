import { Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { SimpleStubFormEngine } from './simple-stub-form.helper';
import { WizardDestinationFormApi } from './destination-form-api';

/** Minimal stand-in form for the Tableau destination type — see SimpleStubFormEngine. */
@Component({
  selector: 'app-tableau-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './simple-stub-destination-form.component.html',
})
export class TableauDestinationFormComponent implements WizardDestinationFormApi {
  private readonly engine = new SimpleStubFormEngine(inject(FormBuilder), 'Tableau Destination', [
    { key: 'workbookName', label: 'Workbook name' },
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
