import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SetSystemSettingRequest, SystemSetting } from '../../models/system-setting.model';
import { describeSettingField, SettingFieldDescriptor } from '../../utils/terminology-setting-field';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

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
    MatSlideToggleModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './system-setting-dialog.component.html',
  styleUrls: ['./system-setting-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically.
export class SystemSettingDialogComponent {
  private readonly svc = inject(ISystemSettingsService);
  private readonly fb  = inject(FormBuilder);
  readonly dialogRef    = inject<DialogRef<boolean>>(DialogRef);
  readonly data         = inject<SystemSettingDialogData>(DIALOG_DATA);

  readonly isEdit        = this.data.mode === 'edit';
  readonly submitted     = signal(false);
  readonly saving        = signal(false);
  readonly errorMessage  = signal<string | null>(null);

  // Only edit mode can infer a control type from the key — a brand-new key typed into "Add
  // Setting" has no established meaning yet, so create mode keeps the plain free-text field.
  readonly fieldDescriptor: SettingFieldDescriptor =
    this.isEdit && this.data.setting ? describeSettingField(this.data.setting.key) : { kind: 'text' };

  readonly form = this.fb.group({
    key: [
      { value: this.data.setting?.key ?? '', disabled: this.isEdit },
      [Validators.required, Validators.maxLength(200)],
    ],
    value: [this.initialValue(), [Validators.required, Validators.maxLength(2000)]],
    description: [this.data.setting?.description ?? '', [Validators.maxLength(500)]],
  });

  get frequencyOptions(): string[] {
    return this.fieldDescriptor.kind === 'frequency' ? this.fieldDescriptor.options : [];
  }

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  private initialValue(): string | boolean {
    const raw = this.data.setting?.value ?? '';
    return this.fieldDescriptor.kind === 'toggle' ? raw.toLowerCase() === 'true' : raw;
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const raw = this.form.getRawValue();
    const key = raw.key!.trim();
    const value = this.fieldDescriptor.kind === 'toggle'
      ? (raw.value ? 'True' : 'False')
      : String(raw.value ?? '').trim();
    const request: SetSystemSettingRequest = {
      value,
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
