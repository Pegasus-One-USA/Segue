import { Component, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/**
 * The "Fetch missing data" switch: when the EHR leaves out data the check needs, Segue reads it from the EHR's own
 * FHIR server with the short-lived token the EHR sent on that call. Nothing fetched is stored.
 */
@Component({
  selector: 'app-cds-fetch-missing-data',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './cds-fetch-missing-data.component.html',
  styleUrl: './cds-fetch-missing-data.component.scss',
})
export class CdsFetchMissingDataComponent {
  readonly control = input.required<FormControl<boolean>>();
}
