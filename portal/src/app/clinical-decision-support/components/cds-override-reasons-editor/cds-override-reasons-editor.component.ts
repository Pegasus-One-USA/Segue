import { Component, input } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CdsOverrideReasonOption } from '../../models/cds.models';
import {
  CDS_DEFAULT_OVERRIDE_REASONS,
  CDS_OVERRIDE_REASON_MAX,
  CdsReasonGroup,
  CdsReasonsArray,
  makeCdsReasonGroup,
} from '../../models/cds-service-options.util';

/**
 * The reasons a clinician can pick when dismissing a card. An empty list means the standard reasons. Codes are
 * what the health system maps to its own override reasons in the EHR, so they should not change once live.
 */
@Component({
  selector: 'app-cds-override-reasons-editor',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, MatTooltipModule],
  templateUrl: './cds-override-reasons-editor.component.html',
  styleUrl: './cds-override-reasons-editor.component.scss',
})
export class CdsOverrideReasonsEditorComponent {
  readonly reasons = input.required<CdsReasonsArray>();
  /** The standard reasons, shown while the service has no list of its own. */
  readonly defaults = input<readonly CdsOverrideReasonOption[]>(CDS_DEFAULT_OVERRIDE_REASONS);
  readonly showAllErrors = input(false);

  readonly max = CDS_OVERRIDE_REASON_MAX;

  /** Starts an own list from the standard reasons, so the admin edits rather than retypes them. */
  customize(): void {
    const array = this.reasons();
    if (array.disabled) return;
    for (const reason of this.defaults()) array.push(makeCdsReasonGroup(reason));
    array.markAsDirty();
  }

  useDefaults(): void {
    const array = this.reasons();
    if (array.disabled) return;
    array.clear();
    array.markAsDirty();
  }

  add(): void {
    const array = this.reasons();
    if (array.disabled || array.length >= this.max) return;
    array.push(makeCdsReasonGroup());
    array.markAsDirty();
  }

  remove(index: number): void {
    const array = this.reasons();
    if (array.disabled || array.length <= 1) return;
    array.removeAt(index);
    array.markAsDirty();
  }

  showError(row: CdsReasonGroup, key: 'code' | 'display'): boolean {
    const control = row.controls[key];
    return control.invalid && (control.touched || this.showAllErrors());
  }
}
