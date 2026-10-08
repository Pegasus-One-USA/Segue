import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';

/** One resource type on the write-back "Data groups" step: either tickable, or greyed with the reason. */
export interface EhrWriteTypeRow {
  resourceType: string;
  /** False = greyed: the EHR (or this source) cannot take it, so it cannot be ticked. */
  selectable: boolean;
  /** Why a greyed row cannot be ticked. Null for a selectable row. */
  reason: string | null;
  /** A selectable row's caveat (e.g. still a dry run until the vendor's write APIs are activated). */
  note: string | null;
}

/** The Step 1 opt-ins some write APIs need on top of the type being selected (the writer skips them otherwise). */
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
  /** The destination's Step 1 opt-ins. Omitted = not checked (every API counts as enabled). */
  optIns?: EhrWriteOptIns;
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

/** How the reasons name a vendor. */
const VENDOR_LABELS: Record<string, string> = {
  Epic: 'Epic',
  Healow: 'eClinicalWorks',
  Athenahealth: 'athenahealth',
  GenericFhir: 'this FHIR server',
};

export const NEEDS_TABULAR_SOURCE_REASON = 'Needs a CSV / SQL Table source';
export const NOT_READ_BY_SOURCE_REASON = 'Not read by this destination\'s source';
export const HOLDER_ENCOUNTER_REASON = 'Turn on "File medical and surgical history on a new telephone encounter" in Step 1';
export const AWAITING_ACTIVATION_NOTE =
  'Sent as a dry run until Vendor write APIs activated is ticked on the connection';
export const DRY_RUN_ONLY_NOTE = 'Sent as a dry run only for now';
export const PATIENT_OPT_IN_NOTE = 'Created only when "Create patient if missing" is on in Step 1';

export function notAcceptedReason(vendor: string | null): string {
  return `Not accepted by ${(vendor && VENDOR_LABELS[vendor]) || vendor || 'this EHR'}`;
}

export function variantOptInReason(variants: readonly string[]): string {
  const names = [...new Set(variants)].map((v) => `"${VARIANT_LABELS[v] ?? v}"`);
  return `Enable ${names.join(' or ')} in Step 1`;
}

/** The Step 1 opt-ins as saved on the destination's fields (EhrWriteBackDestinationFormComponent.getFullConfig). */
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

/**
 * Classifies each candidate type for an EHR write-back destination. Greyed (cannot be ticked):
 * - the EHR does not accept the type at all;
 * - every way the EHR files it needs the target's own ids (requiresTargetReferences) and the source is not a
 *   CSV / SQL Table source;
 * - every remaining way needs a Step 1 opt-in that is off (an API variant, or a holder encounter);
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
  const caps = ctx.capabilities.filter((c) => c.resourceType === resourceType);
  if (caps.length === 0) {
    return { resourceType, selectable: false, reason: notAcceptedReason(ctx.vendor), note: null };
  }

  const fromSource = ctx.sourceIsTabular ? caps : caps.filter((c) => c.requiresTargetReferences !== true);
  if (fromSource.length === 0) {
    return { resourceType, selectable: false, reason: NEEDS_TABULAR_SOURCE_REASON, note: null };
  }

  const usable = fromSource.filter((c) => isOptedIn(c, ctx.optIns));
  if (usable.length === 0) {
    const variants = fromSource.filter((c) => c.requiresVariantOptIn === true && c.variant).map((c) => c.variant!);
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
