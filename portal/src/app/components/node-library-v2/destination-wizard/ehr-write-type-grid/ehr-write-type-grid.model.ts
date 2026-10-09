import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { WRITE_VENDOR_LABELS, isEhrWriteVendor } from '../../../../connections/ehr-write-vendors';
import type { EhrMissingOptIn } from '../destination-forms/ehr-write-back/ehr-write-back.model';

/** One resource type on the write-back "Resource types" step: either tickable, or greyed with the reason. */
export interface EhrWriteTypeRow {
  resourceType: string;
  /** False = greyed: the EHR (or this source) cannot take it, so it cannot be ticked. */
  selectable: boolean;
  /** Why a greyed row cannot be ticked. Null for a selectable row. */
  reason: string | null;
  /** A selectable row's caveat (e.g. still a dry run until the vendor's write APIs are activated). */
  note: string | null;
}

/** The opt-ins (under Options) some write APIs need on top of the type being selected (the writer skips them
 *  otherwise). */
export interface EhrWriteOptIns {
  /** dest_enabledVariants: variants enabled for an API with requiresVariantOptIn. */
  enabledVariants: readonly string[];
  /** dest_createHolderEncounter: history items filed on an encounter the bridge creates (createsHolderEncounter). */
  createHolderEncounter: boolean;
  /** dest_createPatientIfMissing: a Patient is created only through this opt-in (optInOnly). */
  createPatientIfMissing: boolean;
}

/** Everything the Step 2 grid needs to know about the write-back target (EhrWriteTargetService.load). */
export interface EhrWriteTarget {
  /** SourceSystemType of the target, for the "not accepted" reason. */
  vendor: string | null;
  /** What the target EHR accepts (EhrWriteCapabilitiesService). */
  capabilities: readonly EhrWriteCapability[];
  /** The target connection has "Vendor write APIs activated" ticked (always true for a test-as run). */
  vendorWriteApisActivated: boolean;
  optIns: EhrWriteOptIns;
}

export interface EhrWriteTypeContext {
  /** The types to show, in order: the source's declared types, or (legacy source with no list) what the EHR accepts. */
  candidates: readonly string[];
  /** What the target EHR accepts (EhrWriteCapabilitiesService). */
  capabilities: readonly EhrWriteCapability[];
  /** SourceSystemType of the target, for the "not accepted" reason. */
  vendor: string | null;
  /** Every upstream source is a CSV / SQL Table source (the only kind carrying the target EHR's own ids). */
  sourceIsTabular: boolean;
  /** The target connection has "Vendor write APIs activated" ticked (always true for a test-as run). */
  vendorWriteApisActivated: boolean;
  /** The destination's opt-ins (under Options). Omitted = not checked (every API counts as enabled). */
  optIns?: EhrWriteOptIns;
  /** The opt-ins are chosen later, under Options (the step after Resource types): a type that only lacks an opt-in
   *  stays selectable, with a note naming the option it needs. The Options step then blocks Next until it is on. */
  optInsLater?: boolean;
  /** The destination's current selection. A selected type that is not a candidate still gets a (greyed) row, so it
   *  can be unticked instead of staying selected out of sight. */
  selected?: readonly string[];
  /** The candidates are the source's declared types (not the legacy "what the EHR accepts" fallback) — names the
   *  reason of a selected type outside them. */
  sourceDeclaresTypes?: boolean;
}

/** Plain names for the variants a destination enables (EhrWriteVariants). */
export const VARIANT_LABELS: Record<string, string> = {
  'lines-drains-airways': 'Lines, drains and airways',
  'dicom-image-characteristics': 'DICOM image characteristics (CT dose)',
  'radiotherapy-volume': 'Radiotherapy volumes',
  'external-radiotherapy-summary': 'External radiotherapy summaries',
  'document-information': 'Scanned document information (Hyperdrive scanning only)',
  'non-patient-document': 'Non-patient documents',
  'community-resource-message': 'Community resource referral messages',
  'patient-entered-questionnaire': 'Patient-entered questionnaire answers',
};

/** How the reasons name a vendor: its write label, a plain FHIR server as "this FHIR server". */
function reasonVendorLabel(vendor: string | null): string {
  if (vendor === 'GenericFhir') return 'this FHIR server';
  return (isEhrWriteVendor(vendor) && WRITE_VENDOR_LABELS[vendor]) || vendor || 'this EHR';
}

export const NEEDS_TABULAR_SOURCE_REASON = 'Needs a CSV / SQL Table source';
export const NOT_READ_BY_SOURCE_REASON = 'Not read by this destination\'s source';
/** The Options checkbox a holder encounter needs, as it reads there. */
export const HOLDER_ENCOUNTER_OPTION = 'File medical and surgical history on a new telephone encounter';
export const HOLDER_ENCOUNTER_REASON = `Turn on "${HOLDER_ENCOUNTER_OPTION}" under Options`;
export const AWAITING_ACTIVATION_NOTE =
  'Sent as a dry run until Vendor write APIs activated is ticked on the connection';
export const DRY_RUN_ONLY_NOTE = 'Sent as a dry run only for now';
export const PATIENT_OPT_IN_NOTE = 'Created only when "Create the patient when the EHR has no match" is on under Options';

export function notAcceptedReason(vendor: string | null): string {
  return `Not accepted by ${reasonVendorLabel(vendor)}`;
}

export function variantOptInReason(variants: readonly string[]): string {
  const names = [...new Set(variants)].map((v) => `"${VARIANT_LABELS[v] ?? v}"`);
  return `Enable ${names.join(' or ')} under Options`;
}

/** The note on a type that is selectable now but needs an option turned on in the next step. */
export function optInLaterNote(variants: readonly string[], holderEncounter: boolean): string {
  const names = variants.length
    ? [...new Set(variants)].map((v) => `"${VARIANT_LABELS[v] ?? v}"`).join(' or ')
    : holderEncounter ? `"${HOLDER_ENCOUNTER_OPTION}"` : 'an option';
  return `Needs ${names} turned on under Options (next step)`;
}

/** The opt-ins as saved on the destination's fields (EhrWriteBackDestinationFormComponent.getFullConfig). */
export function ehrWriteOptInsOf(fields: Record<string, string>): EhrWriteOptIns {
  return {
    enabledVariants: (fields['dest_enabledVariants'] ?? '').split(',').map((v) => v.trim()).filter(Boolean),
    createHolderEncounter: fields['dest_createHolderEncounter'] === 'true',
    createPatientIfMissing: fields['dest_createPatientIfMissing'] === 'true',
  };
}

/** An API the writer actually uses for this destination: one needing a variant or a holder encounter counts only
 *  when that opt-in is on (MappedEhrWriteBackDestinationWriter skips it otherwise). */
function isOptedIn(capability: EhrWriteCapability, optIns: EhrWriteOptIns | undefined): boolean {
  if (!optIns) return true;
  if (capability.requiresVariantOptIn === true && !(capability.variant && optIns.enabledVariants.includes(capability.variant))) {
    return false;
  }
  return capability.createsHolderEncounter !== true || optIns.createHolderEncounter;
}

/** The capabilities of one type the writer can use from this source: those needing the target's own ids only when
 *  the source is a CSV / SQL Table source. */
function fromSourceCapabilities(
  capabilities: readonly EhrWriteCapability[],
  resourceType: string,
  sourceIsTabular: boolean,
): EhrWriteCapability[] {
  const caps = capabilities.filter((c) => c.resourceType === resourceType);
  return sourceIsTabular ? caps : caps.filter((c) => c.requiresTargetReferences !== true);
}

/** What one type, usable from this source, still needs turned on: its variant opt-ins, or a holder encounter. */
function neededOptIns(fromSource: readonly EhrWriteCapability[]): { variants: string[]; holderEncounter: boolean } {
  const variants = [...new Set(fromSource.filter((c) => c.requiresVariantOptIn === true && c.variant).map((c) => c.variant!))];
  return { variants, holderEncounter: variants.length === 0 && fromSource.some((c) => c.createsHolderEncounter === true) };
}

/**
 * The selected types the writer would skip until an option is turned on: each has ways to be written from this
 * source, and none is enabled by `optIns`. A Patient created only through "Create the patient…" is never listed: it
 * is still matched without it, so it is a note on the grid, never a block.
 */
export function missingOptInsFor(
  selected: readonly string[],
  capabilities: readonly EhrWriteCapability[],
  optIns: EhrWriteOptIns,
  sourceIsTabular: boolean,
): EhrMissingOptIn[] {
  const missing: EhrMissingOptIn[] = [];
  for (const resourceType of new Set(selected)) {
    const fromSource = fromSourceCapabilities(capabilities, resourceType, sourceIsTabular);
    if (fromSource.length === 0 || fromSource.some((c) => isOptedIn(c, optIns))) continue;
    missing.push({ resourceType, ...neededOptIns(fromSource) });
  }
  return missing;
}

/**
 * Classifies each candidate type for an EHR write-back destination. Greyed (cannot be ticked):
 * - the EHR does not accept the type at all;
 * - every way the EHR files it needs the target's own ids (requiresTargetReferences) and the source is not a
 *   CSV / SQL Table source;
 * - every remaining way needs an opt-in that is off (an API variant, or a holder encounter), unless the opt-ins are
 *   chosen later (optInsLater: selectable, with a note naming the option);
 * - a selected type outside the candidates (no longer read by the source, or no longer accepted) — shown so it can
 *   be unticked.
 * Selectable with a note: every live path needs the vendor's contracted APIs and the connection does not have them
 * activated (still sent as a dry run), the type is dry-run only, or it is a Patient created only through the opt-in.
 */
export function classifyEhrWriteTypes(ctx: EhrWriteTypeContext): EhrWriteTypeRow[] {
  const candidates = new Set(ctx.candidates);
  const rows = [...candidates].map((resourceType) => classifyOne(resourceType, ctx));
  for (const resourceType of new Set(ctx.selected ?? [])) {
    if (candidates.has(resourceType)) continue;
    rows.push(
      ctx.sourceDeclaresTypes
        ? { resourceType, selectable: false, reason: NOT_READ_BY_SOURCE_REASON, note: null }
        : { ...classifyOne(resourceType, ctx), selectable: false },
    );
  }
  return rows;
}

function classifyOne(resourceType: string, ctx: EhrWriteTypeContext): EhrWriteTypeRow {
  if (!ctx.capabilities.some((c) => c.resourceType === resourceType)) {
    return { resourceType, selectable: false, reason: notAcceptedReason(ctx.vendor), note: null };
  }

  const fromSource = fromSourceCapabilities(ctx.capabilities, resourceType, ctx.sourceIsTabular);
  if (fromSource.length === 0) {
    return { resourceType, selectable: false, reason: NEEDS_TABULAR_SOURCE_REASON, note: null };
  }

  const usable = fromSource.filter((c) => isOptedIn(c, ctx.optIns));
  if (usable.length === 0) {
    const { variants, holderEncounter } = neededOptIns(fromSource);
    if (ctx.optInsLater) {
      return { resourceType, selectable: true, reason: null, note: optInLaterNote(variants, holderEncounter) };
    }
    const reason = variants.length ? variantOptInReason(variants) : HOLDER_ENCOUNTER_REASON;
    return { resourceType, selectable: false, reason, note: null };
  }

  const live = usable.filter((c) => c.liveWriteSupported);
  let note: string | null = null;
  if (live.length === 0) {
    note = DRY_RUN_ONLY_NOTE;
  } else if (!ctx.vendorWriteApisActivated && live.every((c) => c.requiresVendorActivation === true)) {
    note = AWAITING_ACTIVATION_NOTE;
  } else if (ctx.optIns && !ctx.optIns.createPatientIfMissing && usable.every((c) => c.optInOnly)) {
    note = PATIENT_OPT_IN_NOTE;
  }
  return { resourceType, selectable: true, reason: null, note };
}
