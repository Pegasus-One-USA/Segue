import { Component, input } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';

/**
 * Step 1 "Configure" form for a SQL Server destination — split out of DestinationWizardComponent's own
 * template so each destination type reads as its own component instead of one shared @if/@else chain.
 * `form` is the SAME FormGroup instance the wizard already owns and uses everywhere else (validation,
 * save payload, schema probing) — this component only renders it, it never owns destination state.
 */
@Component({
  selector: 'app-sql-server-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './sql-server-destination-form.component.html',
  styleUrl: './sql-server-destination-form.component.scss',
})
export class SqlServerDestinationFormComponent {
  readonly form = input.required<FormGroup>();
  readonly probeState = input.required<'idle' | 'testing' | 'ok' | 'error'>();
  readonly probeError = input<string | null>(null);
  readonly tableCount = input<number>(0);
}
