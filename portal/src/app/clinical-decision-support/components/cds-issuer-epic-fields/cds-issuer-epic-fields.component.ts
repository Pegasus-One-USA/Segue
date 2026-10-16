import { Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';

/**
 * The trusted-issuer fields added for Epic: the token subject (the CDS client id), the FHIR server Segue may read
 * missing data from, and whether it may. Also explains the key-set (jku) check that Epic tokens get.
 */
@Component({
  selector: 'app-cds-issuer-epic-fields',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule],
  templateUrl: './cds-issuer-epic-fields.component.html',
  styleUrl: './cds-issuer-epic-fields.component.scss',
})
export class CdsIssuerEpicFieldsComponent {
  readonly expectedSubject = input.required<FormControl<string>>();
  readonly fhirBaseUrl = input.required<FormControl<string>>();
  readonly allowCallback = input.required<FormControl<boolean>>();
  /** The issuer (iss) being typed, shown as the FHIR server used when the field is blank. */
  readonly issuer = input('');
  readonly showAllErrors = input(false);

  showError(control: FormControl<string>, error?: string): boolean {
    const visible = control.touched || this.showAllErrors();
    return visible && (error ? control.hasError(error) : control.invalid);
  }
}
