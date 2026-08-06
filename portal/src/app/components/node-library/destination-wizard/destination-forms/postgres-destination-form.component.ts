import { Component, input } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';

/**
 * Step 1 "Configure" form for a PostgreSQL destination — see SqlServerDestinationFormComponent's doc
 * comment for why this is its own component despite sharing the wizard's `sqlForm` FormGroup shape
 * (only the auth options and the SSL toggle actually differ from SQL Server; identical to MySQL's).
 */
@Component({
  selector: 'app-postgres-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './postgres-destination-form.component.html',
  styleUrl: './postgres-destination-form.component.scss',
})
export class PostgresDestinationFormComponent {
  readonly form = input.required<FormGroup>();
  readonly probeState = input.required<'idle' | 'testing' | 'ok' | 'error'>();
  readonly probeError = input<string | null>(null);
  readonly tableCount = input<number>(0);
}
