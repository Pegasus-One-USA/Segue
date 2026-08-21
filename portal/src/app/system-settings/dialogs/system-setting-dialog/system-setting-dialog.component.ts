import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SetSystemSettingRequest, SystemSetting } from '../../models/system-setting.model';

export interface SystemSettingDialogData {
  mode: 'create' | 'edit';
  setting?: SystemSetting;
}

@Component({
  selector: 'app-system-setting-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './system-setting-dialog.component.html',
  styleUrls: ['./system-setting-dialog.component.scss'],
})
export class SystemSettingDialogComponent {
  private readonly svc  = inject(ISystemSettingsService);
  private readonly fb   = inject(FormBuilder);
  readonly dialogRef     = inject(MatDialogRef<SystemSettingDialogComponent>);
  readonly data          = inject<SystemSettingDialogData>(MAT_DIALOG_DATA);

  readonly isEdit        = this.data.mode === 'edit';
  readonly submitted     = signal(false);
  readonly saving        = signal(false);
  readonly errorMessage  = signal<string | null>(null);

  readonly form = this.fb.group({
    key: [
      { value: this.data.setting?.key ?? '', disabled: this.isEdit },
      [Validators.required, Validators.maxLength(200)],
    ],
    value: [this.data.setting?.value ?? '', [Validators.required, Validators.maxLength(2000)]],
    description: [this.data.setting?.description ?? '', [Validators.maxLength(500)]],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const raw = this.form.getRawValue();
    const key = raw.key!.trim();
    const request: SetSystemSettingRequest = {
      value: raw.value!.trim(),
      description: raw.description?.trim() || null,
    };
    this.saving.set(true);

    this.svc.set(key, request).subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save setting. Please try again.');
      },
    });
  }
}
