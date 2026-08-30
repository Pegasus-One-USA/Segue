import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IEhrEndpointService } from '../../services/i-ehr-endpoint.service';
import { EhrEndpoint, EhrEndpointRequest, EhrEndpointType, EhrVendor } from '../../models/ehr-endpoint.model';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface EhrEndpointDialogData {
  endpoint?: EhrEndpoint;
}

export const EHR_VENDOR_OPTIONS: { value: EhrVendor; label: string }[] = [
  { value: 'Epic',               label: 'Epic' },
  { value: 'Cerner',             label: 'Cerner (Oracle Health)' },
  { value: 'Athenahealth',       label: 'Athenahealth' },
  { value: 'Allscripts',         label: 'Allscripts (Veradigm)' },
  { value: 'Healow',             label: 'Healow (eClinicalWorks)' },
  { value: 'MeditechGreenfield', label: 'MEDITECH Greenfield' },
  { value: 'GenericFhir',        label: 'Generic FHIR R4' },
  { value: 'Hl7v2',              label: 'HL7 v2 / MLLP' },
  { value: 'Sample',             label: 'Sample (sandbox)' },
  { value: 'NewEHR',             label: 'New EHR' },
  { value: 'NewEHRTwo',          label: 'New EHR (2)' },
];

export const EHR_ENDPOINT_TYPE_OPTIONS: { value: EhrEndpointType; label: string }[] = [
  { value: 'MyChart', label: 'MyChart (customer production instance)' },
  { value: 'Epic',    label: 'Epic (vendor sandbox)' },
];

@Component({
  selector: 'app-ehr-endpoint-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './ehr-endpoint-dialog.component.html',
  styleUrls: ['./ehr-endpoint-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically.
export class EhrEndpointDialogComponent {
  private readonly svc = inject(IEhrEndpointService);
  private readonly fb  = inject(FormBuilder);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly dialogRef   = inject<DialogRef<boolean>>(DialogRef);
  readonly data: EhrEndpointDialogData = inject(DIALOG_DATA) as EhrEndpointDialogData;

  readonly isEdit       = !!this.data?.endpoint;
  readonly vendorOptions = EHR_VENDOR_OPTIONS;
  readonly endpointTypeOptions = EHR_ENDPOINT_TYPE_OPTIONS;
  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    vendor: [
      this.data?.endpoint?.vendor ?? ('Epic' as EhrVendor),
      [Validators.required],
    ],
    vendorEndpointId: [
      this.data?.endpoint?.vendorEndpointId ?? '',
      [Validators.required, Validators.maxLength(100)],
    ],
    name: [
      this.data?.endpoint?.name ?? '',
      [Validators.required, Validators.maxLength(300)],
    ],
    fhirBaseUrl: [
      this.data?.endpoint?.fhirBaseUrl ?? '',
      [Validators.required, Validators.maxLength(500)],
    ],
    formatType: [
      this.data?.endpoint?.formatType ?? 'R4',
      [Validators.required, Validators.maxLength(20)],
    ],
    status: [
      this.data?.endpoint?.status ?? 'active',
      [Validators.required, Validators.maxLength(50)],
    ],
    endpointType: [
      this.data?.endpoint?.endpointType ?? ('MyChart' as EhrEndpointType),
      [Validators.required],
    ],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    // Defense-in-depth: EhrEndpointListComponent.openAdd()/openEdit() already checked before this
    // dialog opened — re-checked here against a permission change landing mid-edit.
    const requiredCode = this.isEdit ? 'ehrendpoints.edit' : 'ehrendpoints.create';
    if (!this.actionGuard.ensure(requiredCode, `You do not have permission to ${this.isEdit ? 'edit' : 'create'} EHR endpoints.`)) return;
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const request = this.form.getRawValue() as EhrEndpointRequest;
    this.saving.set(true);

    const request$ = this.isEdit
      ? this.svc.update(this.data.endpoint!.id, request)
      : this.svc.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save endpoint. Please try again.');
      },
    });
  }
}
