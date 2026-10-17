import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';
import { WRITE_VENDOR_LABELS, isEhrWriteVendor } from '../../../../connections/ehr-write-vendors';
import { EhrWriteKind, fromSourceCapabilities, kindsFor } from './ehr-write-kinds.model';

/** One resource type on the write-back "Resource types" step: either tickable, or greyed with the reason. */
export interface EhrWriteTypeRow {
  resourceType: string;
  /** False = greyed: the EHR (or this source) cannot take it, so it cannot be ticked. */
  selectable: boolean;
  /** Why a greyed row cannot be ticked. Null for a selectable row. */
  reason: string | null;
  /** A selectable row's caveat (e.g. still a dry run until the vendor's write APIs are activated). */
  note: string | null;
  /** The kinds of record shown under a selectable type (kindsFor); empty when it is written one way only. */
  kinds: EhrWriteKind[];
}

/** The destination's saved switches: the kinds ticked on Step 2 (variants, holder encounter) and the Options step's
 *  "Create the patient". */
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
  /** The destination's saved switches; only "Create the patient" is read here (the Patient note). */
  optIns?: EhrWriteOptIns;
  /** The destination's current selection. A selected type that is not a candidate still gets a (greyed) row, so it
   *  can be unticked instead of staying selected out of sight. */
  selected?: readonly string[];
  /** The candidates are the source's declared types (not the legacy "what the EHR accepts" fallback) — names the
   *  reason of a selected type outside them. */
  sourceDeclaresTypes?: boolean;
}

/** How the reasons name a vendor: its write label, a plain FHIR server as "this FHIR server". */
function reasonVendorLabel(vendor: string | null): string {
  if (vendor === 'GenericFhir') return 'this FHIR server';
  return (isEhrWriteVendor(vendor) && WRITE_VENDOR_LABELS[vendor]) || vendor || 'this EHR';
}

export const NEEDS_TABULAR_SOURCE_REASON = 'Needs a CSV / SQL Table source';
export const NOT_READ_BY_SOURCE_REASON = 'Not read by this destination\'s source';
export const AWAITING_ACTIVATION_NOTE =
  'Sent as a dry run until Vendor write APIs activated is ticked on the connection';
export const DRY_RUN_ONLY_NOTE = 'Sent as a dry run only for now';
export const PATIENT_OPT_IN_NOTE = 'Created only when "Create the patient when the EHR has no match" is on under Options';

export function notAcceptedReason(vendor: string | null): string {
  return `Not accepted by ${reasonVendorLabel(vendor)}`;
}

/** The opt-ins as saved on the destination's fields (EhrWriteBackDestinationFormComponent.getFullConfig). */
export function ehrWriteOptInsOf(fields: Record<string, string>): EhrWriteOptIns {
  return {
    enabledVariants: (fields['dest_enabledVariants'] ?? '').split(',').map((v) => v.trim()).filter(Boolean),
    createHolderEncounter: fields['dest_createHolderEncounter'] === 'true',
    createPatientIfMissing: fields['dest_createPatientIfMissing'] === 'true',
  };
}

/**
 * Classifies each candidate type for an EHR write-back destination. Greyed (cannot be ticked):
 * - the EHR does not accept the type at all;
 * - every way the EHR files it needs the target's own ids (requiresTargetReferences) and the source is not a
 *   CSV / SQL Table source;
 * - a selected type outside the candidates (no longer read by the source, or no longer accepted) — shown so it can
 *   be unticked.
 * A selectable type carries its kinds (kindsFor), chosen under it on the same step, so no type waits on a later step.
 * Selectable with a note: every live path needs the vendor's contracted APIs and the connection does not have them
 * activated (still sent as a dry run), the type is dry-run only, or it is a Patient created only through the option.
 */
export function classifyEhrWriteTypes(ctx: EhrWriteTypeContext): EhrWriteTypeRow[] {
  const candidates = new Set(ctx.candidates);
  const rows = [...candidates].map((resourceType) => classifyOne(resourceType, ctx));
  for (const resourceType of new Set(ctx.selected ?? [])) {
    if (candidates.has(resourceType)) continue;
    rows.push(
      ctx.sourceDeclaresTypes
        ? { resourceType, selectable: false, reason: NOT_READ_BY_SOURCE_REASON, note: null, kinds: [] }
        : { ...classifyOne(resourceType, ctx), selectable: false, kinds: [] },
    );
  }
  return rows;
}

function classifyOne(resourceType: string, ctx: EhrWriteTypeContext): EhrWriteTypeRow {
  if (!ctx.capabilities.some((c) => c.resourceType === resourceType)) {
    return { resourceType, selectable: false, reason: notAcceptedReason(ctx.vendor), note: null, kinds: [] };
  }

  const fromSource = fromSourceCapabilities(ctx.capabilities, resourceType, ctx.sourceIsTabular);
  if (fromSource.length === 0) {
    return { resourceType, selectable: false, reason: NEEDS_TABULAR_SOURCE_REASON, note: null, kinds: [] };
  }

  const live = fromSource.filter((c) => c.liveWriteSupported);
  let note: string | null = null;
  if (live.length === 0) {
    note = DRY_RUN_ONLY_NOTE;
  } else if (!ctx.vendorWriteApisActivated && live.every((c) => c.requiresVendorActivation === true)) {
    note = AWAITING_ACTIVATION_NOTE;
  } else if (resourceType === 'Patient' && ctx.optIns && !ctx.optIns.createPatientIfMissing
    && fromSource.every((c) => c.optInOnly)) {
    note = PATIENT_OPT_IN_NOTE;
  }
  const kinds = kindsFor(resourceType, ctx.capabilities, ctx.sourceIsTabular);
  return { resourceType, selectable: true, reason: null, note, kinds };
}
