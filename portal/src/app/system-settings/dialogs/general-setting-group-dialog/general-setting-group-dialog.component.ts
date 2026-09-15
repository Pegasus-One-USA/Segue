import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SystemSetting } from '../../models/system-setting.model';
import { describeSettingField, settingFieldLabel } from '../../utils/terminology-setting-field';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

/** Renders today's date under a .NET-style format string, for the Workflow Numbering preview only.
 *
 *  Supports the tokens that appear in a workflow-number date segment (yyyy/yy, MM/M, dd/d) — enough to make
 *  ddMMyy vs yyyyMMdd visibly different, which is the whole purpose. The authoritative rendering is the
 *  server's DateTime.ToString(); this is a best-effort sample, so an unrecognized format simply shows through
 *  as-is rather than throwing inside a computed().
 *
 *  Order matters: the longer token of each pair is replaced first, and replacements write into a placeholder
 *  form so that already-substituted digits can never be re-matched by a later token. */
function formatSampleDate(format: string, now: Date = new Date()): string {
  const pad = (value: number, width: number) => String(value).padStart(width, '0');

  const tokens: Array<[string, string]> = [
    ['yyyy', pad(now.getFullYear(), 4)],
    ['yy', pad(now.getFullYear() % 100, 2)],
    ['MM', pad(now.getMonth() + 1, 2)],
    ['M', String(now.getMonth() + 1)],
    ['dd', pad(now.getDate(), 2)],
    ['d', String(now.getDate())],
  ];

  let result = '';
  let index = 0;
  outer: while (index < format.length) {
    for (const [token, value] of tokens) {
      if (format.startsWith(token, index)) {
        result += value;
        index += token.length;
        continue outer;
      }
    }
    result += format[index];
    index += 1;
  }
  return result;
}

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

  /** "WorkflowNumbering:PadWidth" -> "Pad Width" — the dialog heading already names the group. */
  fieldLabel(key: string): string {
    return settingFieldLabel(key);
  }

  // ── Workflow Numbering extras ────────────────────────────────────────────────
  // This group gets a live sample and a reset-policy warning that the other, purely scalar groups have no
  // use for. Both are driven off the form's own values (not a server round-trip), so they track what the
  // admin is currently typing rather than what is saved.

  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  private readonly numberingKey = (field: string) => `WorkflowNumbering:${field}`;

  readonly isWorkflowNumbering = this.fields.some(f => f.setting.key.startsWith('WorkflowNumbering:'));

  /** Reset policy as it stood when the dialog opened — the baseline the warning compares against. */
  private readonly originalResetPolicy =
    this.data.settings.find(s => s.key === 'WorkflowNumbering:ResetPolicy')?.value ?? null;

  /** A sample of the number the NEXT created workflow would get under the currently entered settings.
   *  The counter is not known client-side, so this shows the padded placeholder rather than a real value —
   *  the point is to make the prefix/date-format/padding choices visible, which a raw format string is not. */
  readonly numberPreview = computed<string | null>(() => {
    if (!this.isWorkflowNumbering) return null;
    const raw = this.formValue() as Record<string, unknown>;

    const prefix = String(raw[this.numberingKey('Prefix')] ?? 'WLW').trim() || 'WLW';
    const dateFormat = String(raw[this.numberingKey('DateFormat')] ?? 'ddMMyy').trim() || 'ddMMyy';
    const padWidthRaw = Number(raw[this.numberingKey('PadWidth')] ?? 4);
    const padWidth = Number.isFinite(padWidthRaw) ? Math.min(Math.max(Math.trunc(padWidthRaw), 1), 12) : 4;

    return `${prefix}-${formatSampleDate(dateFormat)}-${'1'.padStart(padWidth, '0')}`;
  });

  /** Changing the policy changes the period key, so the counter starts over at 1 — which reads as a bug
   *  unless it is called out BEFORE saving. Only shown when the policy actually differs from what was
   *  loaded, so simply reopening the dialog doesn't nag. */
  readonly resetPolicyChanged = computed<boolean>(() => {
    if (!this.isWorkflowNumbering || !this.originalResetPolicy) return false;
    const raw = this.formValue() as Record<string, unknown>;
    const current = String(raw[this.numberingKey('ResetPolicy')] ?? '').trim();
    return !!current && current !== this.originalResetPolicy;
  });

  /** The anchor date only applies to CustomAnchorDate; hide it otherwise rather than leaving an input that
   *  silently does nothing. */
  readonly anchorDateApplies = computed<boolean>(() => {
    const raw = this.formValue() as Record<string, unknown>;
    return String(raw[this.numberingKey('ResetPolicy')] ?? '').trim() === 'CustomAnchorDate';
  });

  isHiddenField(key: string): boolean {
    return key === this.numberingKey('AnchorDate') && !this.anchorDateApplies();
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
