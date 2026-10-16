import { Component, input } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { CdsSettingDescriptor } from '../../models/cds.models';
import { CdsSettingsGroup } from '../../models/cds-settings.util';

/** Renders a handler's settings descriptor as a form. The owning page builds and reads the group (cds-settings.util.ts). */
@Component({
  selector: 'app-cds-settings-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './cds-settings-form.component.html',
  styleUrl: './cds-settings-form.component.scss',
})
export class CdsSettingsFormComponent {
  readonly descriptors = input.required<CdsSettingDescriptor[]>();
  readonly group = input.required<CdsSettingsGroup>();
  /** Shows every field's error, not just touched ones (set after a failed save). */
  readonly showAllErrors = input(false);

  fieldId(key: string): string {
    return `cds-setting-${key}`;
  }

  hasError(key: string): boolean {
    const control = this.group().controls[key];
    return !!control && control.invalid && (control.touched || this.showAllErrors());
  }

  errorText(d: CdsSettingDescriptor): string {
    const errors = this.group().controls[d.key]?.errors ?? {};
    if (errors['required']) return `${d.label} is required.`;
    if (errors['min']) return `Must be ${d.min} or more.`;
    if (errors['max']) return `Must be ${d.max} or less.`;
    return 'Check this value.';
  }

  rangeHint(d: CdsSettingDescriptor): string | null {
    if (d.type !== 'number' || (d.min === null && d.max === null)) return null;
    if (d.min !== null && d.max !== null) return `Between ${d.min} and ${d.max}.`;
    return d.min !== null ? `${d.min} or more.` : `${d.max} or less.`;
  }

  defaultHint(d: CdsSettingDescriptor): string | null {
    // Booleans show their default as the initial tick; lists show it as the initial lines.
    if (d.defaultValue === null || d.defaultValue === undefined) return null;
    if (d.type === 'boolean' || d.type === 'list') return null;
    // An empty default (e.g. optional free text) has nothing to show: "Default: ." would read as a typo.
    if (typeof d.defaultValue === 'string' && d.defaultValue.trim() === '') return null;
    if (d.type === 'select') {
      const option = d.options?.find(o => o.value === d.defaultValue);
      return `Default: ${option?.label ?? d.defaultValue}.`;
    }
    return `Default: ${d.defaultValue}.`;
  }
}
