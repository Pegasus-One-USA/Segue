import { Component, input } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';

/**
 * Step 1 "Configure" form for a MongoDB destination — split out of DestinationWizardComponent's own
 * template so each destination type reads as its own component. `form` is the same `mongoForm`
 * FormGroup instance the wizard already owns and uses everywhere else (validation, save payload) —
 * this component only renders it, it never owns destination state.
 */
@Component({
  selector: 'app-mongo-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './mongo-destination-form.component.html',
  styleUrl: './mongo-destination-form.component.scss',
})
export class MongoDestinationFormComponent {
  readonly form = input.required<FormGroup>();
}
