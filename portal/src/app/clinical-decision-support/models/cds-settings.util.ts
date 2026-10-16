import { FormControl, FormGroup, ValidatorFn, Validators } from '@angular/forms';
import { CdsSettingDescriptor, CdsSettingValue } from './cds.models';

// The settings form is generic: one control per entry in the handler's settings descriptor. List settings are
// edited as one item per line and turned back into a string array on save.

export type CdsSettingsGroup = FormGroup<Record<string, FormControl<unknown>>>;

/** Builds one control per descriptor, seeded with the stored value or the handler's default. */
export function buildCdsSettingsGroup(
  descriptors: CdsSettingDescriptor[],
  values: Record<string, CdsSettingValue> | null | undefined,
): CdsSettingsGroup {
  const controls: Record<string, FormControl<unknown>> = {};
  for (const d of descriptors) {
    const stored = values && d.key in values ? values[d.key] : d.defaultValue;
    controls[d.key] = new FormControl<unknown>(toControlValue(d, stored), validatorsFor(d));
  }
  return new FormGroup(controls);
}

/**
 * Reads the form back into the API's settings object (list → string[], blank number → null). A value equal to
 * the handler's default is sent as null ("use the default", not stored), so the service keeps following the
 * default if a later release changes it (e.g. after clinical review of the SAMPLE reference data).
 */
export function readCdsSettings(
  descriptors: CdsSettingDescriptor[],
  group: CdsSettingsGroup,
): Record<string, CdsSettingValue> {
  const result: Record<string, CdsSettingValue> = {};
  for (const d of descriptors) {
    const value = fromControlValue(d, group.controls[d.key]?.value);
    result[d.key] = sameSettingValue(value, d.defaultValue) ? null : value;
  }
  return result;
}

function sameSettingValue(a: CdsSettingValue | undefined, b: CdsSettingValue | undefined): boolean {
  if (a === null || a === undefined || b === null || b === undefined) return false;
  return JSON.stringify(a) === JSON.stringify(b);
}

/** Splits list text on new lines and commas, trims, and drops blanks and repeats (the API does the same). */
export function parseCdsList(text: string): string[] {
  const seen = new Set<string>();
  for (const part of text.split(/[\n,]/)) {
    const item = part.trim();
    if (item) seen.add(item);
  }
  return [...seen];
}

function toControlValue(d: CdsSettingDescriptor, value: CdsSettingValue): unknown {
  if (d.type === 'list') return Array.isArray(value) ? value.join('\n') : '';
  if (d.type === 'boolean') return value === true;
  if (d.type === 'number') return typeof value === 'number' ? value : null;
  return value === null || value === undefined ? '' : String(value);
}

function fromControlValue(d: CdsSettingDescriptor, raw: unknown): CdsSettingValue {
  if (d.type === 'list') return parseCdsList(typeof raw === 'string' ? raw : '');
  if (d.type === 'boolean') return raw === true;
  if (d.type === 'number') {
    if (raw === null || raw === undefined || raw === '') return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }
  const text = typeof raw === 'string' ? raw.trim() : '';
  return text === '' ? null : text;
}

function validatorsFor(d: CdsSettingDescriptor): ValidatorFn[] {
  const validators: ValidatorFn[] = [];
  // A required setting with a default can be left blank: the API fills in the default.
  if (d.required && d.defaultValue === null && d.type !== 'boolean') validators.push(Validators.required);
  if (d.type === 'number') {
    if (d.min !== null) validators.push(Validators.min(d.min));
    if (d.max !== null) validators.push(Validators.max(d.max));
  }
  return validators;
}
