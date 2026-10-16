import { AbstractControl, FormArray, FormControl, FormGroup, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import {
  CdsCardLinkOption,
  CdsFixCodeMapping,
  CdsFixSettings,
  CdsFixTypeDescriptor,
  CdsLinkType,
  CdsOverrideReasonOption,
  CdsServiceInstance,
  SaveCdsServiceInstanceRequest,
} from './cds.models';
import { absoluteHttpsUrlValidator } from './cds-validators';

// Form plumbing for the per-service options added for Epic: dismiss reasons, card links and one-click fixes. Each
// is its own form (like the settings group), rebuilt from the saved service and read back into the API shape.
// The limits mirror the API's (CdsServiceOptionsRules), so a save the form accepts is not refused for shape.

// ─── Dismiss reasons ────────────────────────────────────────────────────────────

/** The standard reasons, used when a service has no list of its own (same as the API's defaults). */
export const CDS_DEFAULT_OVERRIDE_REASONS: readonly CdsOverrideReasonOption[] = [
  { code: 'patient-tolerated', display: 'Patient has tolerated this before' },
  { code: 'benefit-outweighs-risk', display: 'Benefit outweighs the risk' },
  { code: 'will-monitor', display: 'Will monitor the patient' },
  { code: 'not-applicable', display: 'Alert does not apply to this patient' },
];

export const CDS_OVERRIDE_REASON_MAX = 10;
export const CDS_OVERRIDE_REASON_CODE_PATTERN = /^[A-Za-z0-9_-]+$/;

export type CdsReasonGroup = FormGroup<{ code: FormControl<string>; display: FormControl<string> }>;
export type CdsReasonsArray = FormArray<CdsReasonGroup>;

export function makeCdsReasonGroup(reason?: CdsOverrideReasonOption): CdsReasonGroup {
  return new FormGroup({
    code: new FormControl(reason?.code ?? '', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(50), Validators.pattern(CDS_OVERRIDE_REASON_CODE_PATTERN)],
    }),
    display: new FormControl(reason?.display ?? '', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(200)],
    }),
  });
}

/** Codes must be unique, ignoring case. Error key on the array: `duplicateCode` (the repeated code). */
const uniqueReasonCodes: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const seen = new Set<string>();
  for (const group of (control as CdsReasonsArray).controls) {
    const code = group.controls.code.value.trim().toLowerCase();
    if (!code) continue;
    if (seen.has(code)) return { duplicateCode: group.controls.code.value.trim() };
    seen.add(code);
  }
  return null;
};

/** Empty array = the service uses the standard reasons. */
export function buildCdsReasonsArray(instance: CdsServiceInstance | null): CdsReasonsArray {
  const own = instance && !instance.overrideReasonsAreDefault ? instance.overrideReasons : [];
  return new FormArray(own.map(r => makeCdsReasonGroup(r)), { validators: [uniqueReasonCodes] });
}

/** Null when the list is empty, so the service keeps following the standard reasons. */
export function readCdsReasons(array: CdsReasonsArray): CdsOverrideReasonOption[] | null {
  const reasons = array.getRawValue().map(r => ({ code: r.code.trim(), display: r.display.trim() }));
  return reasons.length ? reasons : null;
}

// ─── Card links ─────────────────────────────────────────────────────────────────

export const CDS_LINK_MAX = 10;

export type CdsLinkGroup = FormGroup<{
  label: FormControl<string>;
  url: FormControl<string>;
  type: FormControl<CdsLinkType>;
  appContext: FormControl<string>;
  autolaunchable: FormControl<boolean>;
}>;
export type CdsLinksArray = FormArray<CdsLinkGroup>;

export function makeCdsLinkGroup(link?: CdsCardLinkOption): CdsLinkGroup {
  return new FormGroup({
    label: new FormControl(link?.label ?? '', { nonNullable: true, validators: [Validators.required, Validators.maxLength(100)] }),
    url: new FormControl(link?.url ?? '', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(1000), absoluteHttpsUrlValidator],
    }),
    type: new FormControl<CdsLinkType>(link?.type === 'smart' ? 'smart' : 'absolute', { nonNullable: true }),
    appContext: new FormControl(link?.appContext ?? '', { nonNullable: true, validators: [Validators.maxLength(1000)] }),
    autolaunchable: new FormControl(link?.autolaunchable === true, { nonNullable: true }),
  });
}

export function buildCdsLinksArray(instance: CdsServiceInstance | null): CdsLinksArray {
  return new FormArray((instance?.links ?? []).map(l => makeCdsLinkGroup(l)));
}

/** App context and auto-launch belong to SMART app links only, so they are dropped from web links. */
export function readCdsLinks(array: CdsLinksArray): CdsCardLinkOption[] | null {
  const links = array.getRawValue().map(l => {
    const smart = l.type === 'smart';
    return {
      label: l.label.trim(),
      url: l.url.trim(),
      type: l.type,
      appContext: smart ? l.appContext.trim() || null : null,
      autolaunchable: smart && l.autolaunchable,
    };
  });
  return links.length ? links : null;
}

// ─── One-click fixes ────────────────────────────────────────────────────────────

export const CDS_FIX_MAPPINGS_MAX = 50;

export type CdsMappingGroup = FormGroup<{
  trigger: FormControl<string>;
  system: FormControl<string>;
  code: FormControl<string>;
  display: FormControl<string>;
}>;

export type CdsFixTypeGroup = FormGroup<{
  key: FormControl<string>;
  enabled: FormControl<boolean>;
  mappings: FormArray<CdsMappingGroup>;
}>;

export type CdsFixesGroup = FormGroup<{
  enabled: FormControl<boolean>;
  recommendedFixType: FormControl<string>;
  fixTypes: FormArray<CdsFixTypeGroup>;
}>;

/** One mapping row. Display is required only where the fix type says so. */
export function makeCdsMappingGroup(descriptor: CdsFixTypeDescriptor, mapping?: CdsFixCodeMapping): CdsMappingGroup {
  const displayRequired = descriptor.mappingFields.some(f => f.key === 'display' && f.required);
  return new FormGroup({
    trigger: new FormControl(mapping?.trigger ?? '', { nonNullable: true }),
    system: new FormControl(mapping?.system ?? descriptor.codeSystems?.[0]?.value ?? '', {
      nonNullable: true, validators: [Validators.required, Validators.maxLength(200)],
    }),
    code: new FormControl(mapping?.code ?? '', { nonNullable: true, validators: [Validators.required, Validators.maxLength(100)] }),
    display: new FormControl(mapping?.display ?? '', {
      nonNullable: true,
      validators: displayRequired ? [Validators.required, Validators.maxLength(200)] : [Validators.maxLength(200)],
    }),
  });
}

/** Two rows for the same trigger (or two "any" rows). Error key on the mappings array: `duplicateTrigger`. */
const uniqueTriggers: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const seen = new Set<string>();
  for (const row of (control as FormArray<CdsMappingGroup>).controls) {
    const trigger = row.controls.trigger.value.trim();
    if (seen.has(trigger)) return { duplicateTrigger: trigger || 'any' };
    seen.add(trigger);
  }
  return null;
};

/** One group per fix type the check offers, in the check's order, seeded with what the service saved. */
export function buildCdsFixesGroup(descriptors: CdsFixTypeDescriptor[], settings: CdsFixSettings | null | undefined): CdsFixesGroup {
  const fixTypes = descriptors.map(d => {
    const saved = settings?.fixTypes.find(t => t.key === d.key);
    return new FormGroup({
      key: new FormControl(d.key, { nonNullable: true }),
      enabled: new FormControl(saved?.enabled === true, { nonNullable: true }),
      mappings: new FormArray((saved?.mappings ?? []).map(m => makeCdsMappingGroup(d, m)), { validators: [uniqueTriggers] }),
    });
  });
  const recommended = settings?.recommendedFixType ?? '';
  return new FormGroup({
    enabled: new FormControl(settings?.enabled === true && descriptors.length > 0, { nonNullable: true }),
    recommendedFixType: new FormControl(descriptors.some(d => d.key === recommended) ? recommended : '', { nonNullable: true }),
    fixTypes: new FormArray(fixTypes),
  });
}

/** True when a switched-on fix type still needs a mapping its check requires. */
export function cdsFixTypeNeedsMapping(descriptor: CdsFixTypeDescriptor | undefined, group: CdsFixTypeGroup): boolean {
  return !!descriptor && descriptor.requiresMapping && group.controls.enabled.value && group.controls.mappings.length === 0;
}

/** True when "Offer one-click fixes" is on but no fix type is ticked (no card could carry a fix). */
export function cdsFixesNoTypeOn(group: CdsFixesGroup): boolean {
  return group.controls.enabled.value && !group.controls.fixTypes.controls.some(t => t.controls.enabled.value);
}

/**
 * Whether the fixes can be saved as they are. While fixes are on, at least one fix type must be ticked, and each
 * ticked type must be complete; anything else is saved in a form the API always accepts (see readCdsFixes).
 */
export function cdsFixesInvalid(descriptors: CdsFixTypeDescriptor[], group: CdsFixesGroup): boolean {
  if (!group.controls.enabled.value) return false;
  if (cdsFixesNoTypeOn(group)) return true;
  return group.controls.fixTypes.controls.some((t, i) =>
    t.controls.enabled.value && (t.controls.mappings.invalid || cdsFixTypeNeedsMapping(descriptors[i], t)));
}

/**
 * The API shape, or null when the check offers no fixes. A fix type that would not pass the API's rules (only
 * possible while fixes are off, since the form blocks it otherwise) is saved switched off, with only its complete
 * mappings, so turning fixes off never fails a save. The recommended fix is kept only while its type is on.
 */
export function readCdsFixes(descriptors: CdsFixTypeDescriptor[], group: CdsFixesGroup): CdsFixSettings | null {
  if (descriptors.length === 0) return null;
  const fixTypes = group.controls.fixTypes.controls.map((t, i) => {
    const descriptor = descriptors[i];
    const valid = !t.controls.mappings.invalid && !cdsFixTypeNeedsMapping(descriptor, t);
    const rows = t.controls.mappings.controls.filter(row => valid || row.valid);
    const mappings = dedupeByTrigger(rows.map(row => toMapping(row)));
    const stillNeedsMapping = descriptor.requiresMapping && mappings.length === 0;
    return { key: t.controls.key.value, enabled: t.controls.enabled.value && valid && !stillNeedsMapping, mappings };
  });

  const enabled = group.controls.enabled.value;
  const recommended = group.controls.recommendedFixType.value;
  const recommendedOn = fixTypes.some(t => t.key === recommended && t.enabled);
  const anyConfigured = enabled || fixTypes.some(t => t.enabled || t.mappings.length > 0);
  if (!anyConfigured) return null;
  return { enabled, fixTypes, recommendedFixType: recommendedOn ? recommended : null };
}

function toMapping(row: CdsMappingGroup): CdsFixCodeMapping {
  const v = row.getRawValue();
  return {
    trigger: v.trigger.trim() || null,
    system: v.system.trim(),
    code: v.code.trim(),
    display: v.display.trim() || null,
  };
}

function dedupeByTrigger(mappings: CdsFixCodeMapping[]): CdsFixCodeMapping[] {
  const seen = new Set<string>();
  return mappings.filter(m => {
    const key = m.trigger ?? '';
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

// ─── Saving without the setup page ──────────────────────────────────────────────

/**
 * The Epic options of a saved service, as a save request sends them. Used by saves that change something else
 * (the list's on/off switch): the API replaces the whole service, so these must be sent back unchanged.
 */
export function cdsSavedOptions(instance: CdsServiceInstance): Pick<
  SaveCdsServiceInstanceRequest, 'fetchMissingData' | 'fixes' | 'overrideReasons' | 'links'
> {
  const fixes = instance.fixes;
  const neverSet = !fixes || (!fixes.enabled && fixes.fixTypes.length === 0);
  return {
    fetchMissingData: instance.fetchMissingData ?? true,
    fixes: neverSet ? null : fixes,
    overrideReasons: instance.overrideReasonsAreDefault ? null : instance.overrideReasons,
    links: instance.links?.length ? instance.links : null,
  };
}
