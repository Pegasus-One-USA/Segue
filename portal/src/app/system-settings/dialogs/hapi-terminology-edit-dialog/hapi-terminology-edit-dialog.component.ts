import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  HapiTerminologyConfiguration,
  HapiTerminologyConfigurationService,
  UpdateHapiTerminologyConfiguration,
} from '../../services/hapi-terminology-configuration.service';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface HapiTerminologyEditDialogData {
  config: HapiTerminologyConfiguration;
}

/** Grouped Edit form for one HAPI-terminology-server sync system — scheduler fields always present,
 * plus a credential field per entry in config.credentials (LOINC: username+password; SNOMED
 * CT/RxNorm: one shared UTS API key). Single Update button saves everything in one call. */
@Component({
  selector: 'app-hapi-terminology-edit-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './hapi-terminology-edit-dialog.component.html',
  styleUrls: ['../../dialogs/system-setting-dialog/system-setting-dialog.component.scss'],
})
export class HapiTerminologyEditDialogComponent {
  private readonly svc = inject(HapiTerminologyConfigurationService);
  private readonly fb = inject(FormBuilder);
  readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
  readonly data = inject<HapiTerminologyEditDialogData>(DIALOG_DATA);

  readonly config = this.data.config;
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    schedulerEnabled: [this.config.schedulerEnabled],
    frequency: [this.config.frequency, [Validators.required]],
    executionTime: [this.config.executionTime, [Validators.required]],
    downloadApiUrl: [this.config.downloadApiUrl ?? ''],
    credentials: this.fb.group(
      Object.fromEntries(this.config.credentials.map((c) => [c.name, ['']])),
    ),
  });

  get hasDownloadApiUrl(): boolean {
    return this.config.downloadApiUrl !== null;
  }

  get credentialControls() {
    return this.config.credentials;
  }

  placeholderFor(hasValue: boolean): string {
    return hasValue ? 'Leave blank to keep current value' : 'Not set';
  }

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const raw = this.form.getRawValue();
    const credentialValues: Record<string, string> = {};
    for (const [name, value] of Object.entries(raw.credentials ?? {})) {
      if (value) credentialValues[name] = String(value).trim();
    }

    const request: UpdateHapiTerminologyConfiguration = {
      schedulerEnabled: !!raw.schedulerEnabled,
      frequency: raw.frequency!,
      executionTime: raw.executionTime!,
      credentialValues: Object.keys(credentialValues).length ? credentialValues : null,
      downloadApiUrl: this.hasDownloadApiUrl ? (raw.downloadApiUrl?.trim() || null) : null,
    };

    this.saving.set(true);
    this.svc.update(this.config.code, request).subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save settings. Please try again.');
      },
    });
  }
}
