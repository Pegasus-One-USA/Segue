import { FormBuilder, FormGroup } from '@angular/forms';
import { Validators } from '@angular/forms';

/** One type-appropriate placeholder field a stub destination form offers beyond name/target — e.g. a
 *  container name for Blob Storage, a bucket name for S3. */
export interface StubExtraField {
  key: string;
  label: string;
  placeholder?: string;
}

/**
 * Shared engine behind every minimal stub destination form (destination-forms/*-destination-form.component.ts
 * for the 15 destination types with no real UI yet) — a `name` field (required) + a `target` field (matches
 * CreateDestinationConfigurationRequest.target) + whatever 1-2 extra placeholder fields that type calls out.
 * Not itself a component (Angular components can't easily share one abstract base's constructor-time field
 * initialization cleanly here) — each stub component composes one of these and delegates
 * WizardDestinationFormApi straight through to it.
 */
export class SimpleStubFormEngine {
  readonly form: FormGroup;

  constructor(
    fb: FormBuilder,
    private readonly defaultName: string,
    readonly extraFields: StubExtraField[] = [],
  ) {
    const controls: Record<string, unknown> = {
      name: [defaultName, [Validators.required]],
      target: [''],
    };
    for (const f of extraFields) controls[f.key] = [''];
    this.form = fb.group(controls);
  }

  isValid(): boolean {
    return this.form.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.form.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.form.getRawValue() as Record<string, string>;
    const config: Record<string, string> = {
      dest_name: v['name'] ?? '',
      dest_target: v['target'] ?? '',
    };
    for (const f of this.extraFields) config[`dest_${f.key}`] = v[f.key] ?? '';
    return config;
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    return { fields: this.getFullConfig(), secret: null };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    const patch: Record<string, string> = {
      name: fields['dest_name'] || this.defaultName,
      target: fields['dest_target'] || target || '',
    };
    for (const f of this.extraFields) patch[f.key] = fields[`dest_${f.key}`] || '';
    this.form.patchValue(patch);
  }

  reset(): void {
    const patch: Record<string, string> = { name: this.defaultName, target: '' };
    for (const f of this.extraFields) patch[f.key] = '';
    this.form.reset(patch);
  }
}
