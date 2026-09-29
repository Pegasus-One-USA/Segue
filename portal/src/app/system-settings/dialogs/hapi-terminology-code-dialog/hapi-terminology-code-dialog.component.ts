import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { HapiTerminologyConfigurationService, TerminologyConcept } from '../../services/hapi-terminology-configuration.service';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface HapiTerminologyCodeDialogData {
  systemCode: string;
  displayName: string;
  concept?: TerminologyConcept;
}

/** Add/edit one code+description row for a HAPI terminology system, stored the same way (TRM_CONCEPT)
 * a synced code would be — see TerminologyConceptService on the backend. */
@Component({
  selector: 'app-hapi-terminology-code-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatCheckboxModule,
  ],
  templateUrl: './hapi-terminology-code-dialog.component.html',
  styleUrls: ['./hapi-terminology-code-dialog.component.scss'],
})
export class HapiTerminologyCodeDialogComponent {
  private readonly svc = inject(HapiTerminologyConfigurationService);
  private readonly fb  = inject(FormBuilder);
  readonly dialogRef   = inject<DialogRef<boolean>>(DialogRef);
  readonly data: HapiTerminologyCodeDialogData = inject(DIALOG_DATA) as HapiTerminologyCodeDialogData;

  readonly isEdit       = !!this.data?.concept;
  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    code: [this.data?.concept?.code ?? '', [
      Validators.required,
      Validators.maxLength(500),
    ]],
    display: [this.data?.concept?.display ?? '', [
      Validators.maxLength(2000),
    ]],
    // Unbounded in storage (see TrmConceptConfiguration) because real source descriptions — wrapped HCPCS
    // long descriptions, MeSH ScopeNotes — genuinely run long; the generous client cap only guards against
    // a paste of something that clearly is not a description.
    shortDescription: [this.data?.concept?.shortDescription ?? '', [Validators.maxLength(4000)]],
    longDescription: [this.data?.concept?.longDescription ?? '', [Validators.maxLength(8000)]],
    longCommonName: [this.data?.concept?.longCommonName ?? '', [Validators.maxLength(4000)]],
    // A manually added code defaults to active, matching the rule applied to synced codes whose source
    // publishes no status signal.
    isActive: [this.data?.concept?.isActive ?? true],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const { code, display, shortDescription, longDescription, longCommonName, isActive } =
      this.form.value as {
        code: string;
        display: string;
        shortDescription: string;
        longDescription: string;
        longCommonName: string;
        isActive: boolean;
      };
    this.saving.set(true);

    const payload = {
      code,
      display: display || null,
      shortDescription: shortDescription || null,
      longDescription: longDescription || null,
      longCommonName: longCommonName || null,
      isActive: isActive ?? true,
    };

    const request$ = this.isEdit
      ? this.svc.updateCode(this.data.systemCode, this.data.concept!.pid, payload)
      : this.svc.addCode(this.data.systemCode, payload);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? err.error?.message ?? 'Failed to save the code. Please try again.');
      },
    });
  }
}
