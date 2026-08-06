import { Component, input } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';

/**
 * Step 1 "Configure" form for a CSV destination — split out of DestinationWizardComponent's own
 * template so each destination type reads as its own component. `form` is the same `csvForm`
 * FormGroup instance the wizard already owns and uses everywhere else (validation, save payload) —
 * this component only renders it, it never owns destination state.
 */
@Component({
  selector: 'app-csv-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './csv-destination-form.component.html',
  styleUrl: './csv-destination-form.component.scss',
})
export class CsvDestinationFormComponent {
  readonly form = input.required<FormGroup>();
}
