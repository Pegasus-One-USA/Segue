import { Component, input } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';

/**
 * Step 1 "Configure" form for a MySQL destination — see SqlServerDestinationFormComponent's doc
 * comment for why this is its own component despite sharing the wizard's `sqlForm` FormGroup shape
 * (only the auth options and the SSL toggle actually differ from SQL Server/Postgres).
 */
@Component({
  selector: 'app-mysql-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './mysql-destination-form.component.html',
  styleUrl: './mysql-destination-form.component.scss',
})
export class MySqlDestinationFormComponent {
  readonly form = input.required<FormGroup>();
  readonly probeState = input.required<'idle' | 'testing' | 'ok' | 'error'>();
  readonly probeError = input<string | null>(null);
  readonly tableCount = input<number>(0);
}
