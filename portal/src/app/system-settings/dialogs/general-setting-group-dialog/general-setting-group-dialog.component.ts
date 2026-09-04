import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SystemSetting } from '../../models/system-setting.model';
import { describeSettingField } from '../../utils/terminology-setting-field';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface GeneralSettingGroupDialogData {
  label: string;
  settings: SystemSetting[];
}

/**
 * Grouped Edit for one General Settings group (e.g. "Alert Evaluation" → AlertEvaluation:Enabled,
 * AlertEvaluation:IntervalSeconds, ...) — generalizes the same "one row per group, one Update saves
 * everything" pattern built for Terminology, but for an arbitrary/unknown set of groups, so field
 * rendering stays driven by describeSettingField() rather than a hand-written form per group.
 *
 * Deliberately plain, hand-styled HTML inputs — no Angular Material — matching the convention used
 * elsewhere in the app (e.g. the Source Connections wizard forms) rather than the Material-based
 * dialogs used for individual System Settings / Terminology edits.
 */
@Component({
  selector: 'app-general-setting-group-dialog',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  templateUrl: './general-setting-group-dialog.component.html',
  styleUrls: ['./general-setting-group-dialog.component.scss'],
})
export class GeneralSettingGroupDialogComponent {
  private readonly svc = inject(ISystemSettingsService);
  private readonly fb = inject(FormBuilder);
  readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
  readonly data = inject<GeneralSettingGroupDialogData>(DIALOG_DATA);

  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly fields = this.data.settings.map((setting) => ({
    setting,
    descriptor: describeSettingField(setting.key),
  }));

  readonly form = this.fb.group(
    Object.fromEntries(this.fields.map(({ setting, descriptor }) => [setting.key, [this.initialValue(setting, descriptor.kind)]])),
  );

  private initialValue(setting: SystemSetting, kind: string): string | boolean {
    return kind === 'toggle' ? setting.value?.toLowerCase() === 'true' : (setting.value ?? '');
  }

  save(): void {
    this.errorMessage.set(null);
    const raw = this.form.getRawValue();

    const items = this.fields.map(({ setting, descriptor }) => ({
      key: setting.key,
      value: descriptor.kind === 'toggle' ? (raw[setting.key] ? 'True' : 'False') : String(raw[setting.key] ?? '').trim(),
      description: setting.description,
    }));

    this.saving.set(true);
    this.svc.setBatch(items).subscribe({
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
