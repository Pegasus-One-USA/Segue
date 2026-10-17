import { Component, input, output } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';

/**
 * "Advanced → Data set identity" of a CSV / SQL source. The key is filled in automatically; it is shown here only for
 * the rare case of replacing a source with a new one that reads the same data.
 */
@Component({
  selector: 'app-tabular-dataset-identity',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './tabular-dataset-identity.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularDatasetIdentityComponent {
  readonly control = input.required<FormControl<string>>();
  /** Opens the section, e.g. when the key needs fixing before saving. */
  readonly open = input(false);
  /** The user typed a key of their own. */
  readonly edited = output<void>();
}
