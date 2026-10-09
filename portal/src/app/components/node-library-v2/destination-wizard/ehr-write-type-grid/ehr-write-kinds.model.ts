import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';

/**
 * The kinds of record an EHR write-back destination writes for one resource type, as Step 2 shows them under the
 * type (e.g. Observation: Vital signs, Lines, drains and airways). Each kind is one way the target EHR files the type
 * (one capability), worked out from the capability list the API returns; nothing here is per vendor except the plain
 * label of each variant.
 *
 * A kind is either always included (the writer picks it from the record's shape, and there is no switch to leave it
 * out) or optional, turned on through one of the destination's saved switches: its variant in dest_enabledVariants,
 * or dest_createHolderEncounter for history filed on an encounter the bridge creates. The switches are the saved
 * fields themselves, so a kind ticked here is exactly what the writer uses.
 */
export interface EhrWriteKind {
  /** Unique within the type: the variant, else the vendor API id. */
  id: string;
  resourceType: string;
  /** Plain name (KIND_LABELS), else the id. */
  label: string;
  /** False = always included whenever the type is ticked. */
  optional: boolean;
  /** The dest_enabledVariants entry that turns it on (requiresVariantOptIn); null when there is none. */
  variant: string | null;
  /** Turned on by dest_createHolderEncounter (createsHolderEncounter). */
  holderEncounter: boolean;
}

/** The saved switches the optional kinds are turned on with (dest_enabledVariants, dest_createHolderEncounter). */
export interface EhrWriteKindSwitches {
  enabledVariants: readonly string[];
  createHolderEncounter: boolean;
}

/** One selected type as the Review step lists it, with the kinds written. */
export interface EhrReviewTypeLine {
  resourceType: string;
  /** Plain names of the kinds written; empty for a type written one way only. */
  kinds: string[];
}

export const NO_KIND_SWITCHES: EhrWriteKindSwitches = { enabledVariants: [], createHolderEncounter: false };

/** Plain names for the variants an EHR files a type through (EhrWriteVariants). */
export const KIND_LABELS: Record<string, string> = {
  'vital-signs': 'Vital signs',
  'laboratory-result': 'Lab results',
  'lines-drains-airways': 'Lines, drains and airways',
  'dicom-image-characteristics': 'CT radiation dose',
  'clinical-note': 'Clinical notes',
  'document-information': 'Scanned documents',
  'non-patient-document': 'Non-patient documents',
  'radiotherapy-volume': 'Radiotherapy volumes',
  'external-radiotherapy-summary': 'External radiotherapy summaries',
  'community-resource-message': 'Community resource referral messages',
  'patient-entered-questionnaire': 'Patient-entered questionnaire answers',
  'problem-list-item': 'Problem list',
  'encounter-diagnosis': 'Encounter diagnosis',
  'medical-history': 'Medical history',
  'surgical-history': 'Surgical history',
  'historical-immunization': 'Immunization history',
  'medication-list': 'Medication list',
};

export const ALWAYS_INCLUDED_NOTE = 'always included';
/** An always-included kind of a type not ticked: its box is empty, so say when it is sent. */
export function includedWhenTickedNote(resourceType: string): string {
  return `included whenever ${resourceType} is ticked`;
}
/** Shown under a ticked kind filed on a holder encounter: the prerequisite is turned on by ticking it. */
export const HOLDER_ENCOUNTER_KIND_NOTE =
  'Filed on a new telephone encounter, created per patient per run only when a history item is sent.';
/** A selected type with none of its kinds ticked (a destination saved before kinds were chosen here). */
export const NO_KIND_CHOSEN_NOTE = 'Tick at least one, or untick the type.';

/** The capabilities of one type the writer can use from this source: those needing the target's own ids only when
 *  the source is a CSV / SQL Table source. */
export function fromSourceCapabilities(
  capabilities: readonly EhrWriteCapability[],
  resourceType: string,
  sourceIsTabular: boolean,
): EhrWriteCapability[] {
  const caps = capabilities.filter((c) => c.resourceType === resourceType);
  return sourceIsTabular ? caps : caps.filter((c) => c.requiresTargetReferences !== true);
}

/**
 * The kinds Step 2 shows under a type: one per way the EHR files it from this source (a kind needing a CSV / SQL
 * source is left out for any other source). Empty when there is nothing to choose: the type is written one way only,
 * and that way is always included.
 */
export function kindsFor(
  resourceType: string,
  capabilities: readonly EhrWriteCapability[],
  sourceIsTabular: boolean,
): EhrWriteKind[] {
  const kinds: EhrWriteKind[] = [];
  for (const c of fromSourceCapabilities(capabilities, resourceType, sourceIsTabular)) {
    const id = c.variant ?? c.vendorApiId;
    if (kinds.some((k) => k.id === id)) continue;
    const variant = c.requiresVariantOptIn === true && c.variant ? c.variant : null;
    const holderEncounter = c.createsHolderEncounter === true;
    kinds.push({
      id,
      resourceType,
      label: KIND_LABELS[id] ?? id,
      optional: variant !== null || holderEncounter,
      variant,
      holderEncounter,
    });
  }
  return kinds.length > 1 || kinds.some((k) => k.optional) ? kinds : [];
}

/** The type has a kind that is always included, so ticking the type alone writes something. */
export function hasAlwaysIncludedKind(kinds: readonly EhrWriteKind[]): boolean {
  return kinds.length === 0 || kinds.some((k) => !k.optional);
}

/** The kind is written whenever its type is: always included, or its switches are on. */
export function isKindOn(kind: EhrWriteKind, switches: EhrWriteKindSwitches): boolean {
  if (!kind.optional) return true;
  const variantOn = kind.variant === null || switches.enabledVariants.includes(kind.variant);
  const holderOn = !kind.holderEncounter || switches.createHolderEncounter;
  return variantOn && holderOn;
}

/** Turns one kind's switches on or off; an always-included kind has none. */
export function setKind(switches: EhrWriteKindSwitches, kind: EhrWriteKind, on: boolean): EhrWriteKindSwitches {
  let enabledVariants = [...switches.enabledVariants];
  if (kind.variant) {
    enabledVariants = enabledVariants.filter((v) => v !== kind.variant);
    if (on) enabledVariants.push(kind.variant);
  }
  return {
    enabledVariants,
    createHolderEncounter: kind.holderEncounter ? on : switches.createHolderEncounter,
  };
}

/** A type just ticked whose kinds would all be off (it has no always-included kind): every one of them goes on, so a
 *  ticked type always writes something. */
export function withTypeTicked(
  resourceType: string,
  capabilities: readonly EhrWriteCapability[],
  sourceIsTabular: boolean,
  switches: EhrWriteKindSwitches,
): EhrWriteKindSwitches {
  const kinds = kindsFor(resourceType, capabilities, sourceIsTabular);
  if (kinds.length === 0 || kinds.some((k) => isKindOn(k, switches))) return switches;
  return kinds.reduce((s, k) => setKind(s, k, true), switches);
}

/** The switches as saved: only those a kind of a selected type uses (shown from this source), so a type unticked, or
 *  a kind this source cannot feed, leaves nothing behind. Keeps the order they were turned on in. */
export function switchesFor(
  selected: readonly string[],
  capabilities: readonly EhrWriteCapability[],
  sourceIsTabular: boolean,
  switches: EhrWriteKindSwitches,
): EhrWriteKindSwitches {
  const used = [...new Set(selected)].flatMap((t) => kindsFor(t, capabilities, sourceIsTabular)).filter((k) => k.optional);
  return {
    enabledVariants: [...new Set(switches.enabledVariants)].filter((v) => used.some((k) => k.variant === v)),
    createHolderEncounter: switches.createHolderEncounter && used.some((k) => k.holderEncounter),
  };
}

/** Selected types that have kinds but none of them on: the writer would skip them. Only a destination saved before
 *  kinds were chosen here can be in this state; Step 2 waits until each is resolved. */
export function typesWithoutKind(
  selected: readonly string[],
  capabilities: readonly EhrWriteCapability[],
  sourceIsTabular: boolean,
  switches: EhrWriteKindSwitches,
): string[] {
  return [...new Set(selected)].filter((t) => {
    const kinds = kindsFor(t, capabilities, sourceIsTabular);
    return kinds.length > 0 && !kinds.some((k) => isKindOn(k, switches));
  });
}

/**
 * What a kind's switch does to the other ticked types that share it (e.g. one telephone-encounter switch for medical
 * and surgical history), worded for what the next click on this kind actually does; '' when it shares with none.
 * - Switch off: ticking it ticks the kind there too: "Also ticks Surgical history under Procedure".
 * - Switch on, and this type has another kind on: unticking turns the switch off there too, and unticks a type left
 *   with no kind: "Unticking also unticks Surgical history under Procedure, and so Procedure (its only kind)".
 * - Switch on otherwise (unticking only unticks this type, or the type is not ticked): the box is ticked because of
 *   the other type, so say it is the same setting: "Same setting as Surgical history under Procedure".
 */
export function sharedSwitchNote(
  kind: EhrWriteKind,
  rows: readonly { resourceType: string; kinds: readonly EhrWriteKind[] }[],
  selected: readonly string[],
  switches: EhrWriteKindSwitches = NO_KIND_SWITCHES,
): string {
  if (!kind.optional) return '';
  const shares = (other: EhrWriteKind) =>
    (kind.variant !== null && other.variant === kind.variant) || (kind.holderEncounter && other.holderEncounter);
  const others = rows
    .filter((r) => r.resourceType !== kind.resourceType && selected.includes(r.resourceType))
    .flatMap((r) => r.kinds.filter(shares).map((k) => ({ kind: k, rowKinds: r.kinds })));
  if (others.length === 0) return '';
  const named = (o: { kind: EhrWriteKind }) => `${o.kind.label} under ${o.kind.resourceType}`;

  if (!isKindOn(kind, switches)) return `Also ticks ${others.map(named).join(', ')}`;

  const ownKinds = rows.find((r) => r.resourceType === kind.resourceType)?.kinds ?? [];
  const unticksOnlyThisType = !selected.includes(kind.resourceType)
    || !ownKinds.some((k) => k.id !== kind.id && isKindOn(k, switches));
  if (unticksOnlyThisType) return `Same setting as ${others.map(named).join(', ')}`;

  const after = setKind(switches, kind, false);
  const effects = others.map((o) => o.rowKinds.some((k) => isKindOn(k, after))
    ? named(o)
    : `${named(o)}, and so ${o.kind.resourceType} (its only kind)`);
  return `Unticking also unticks ${effects.join('; ')}`;
}

/** The Review step's resource types, each with the kinds written (in plain words). */
export function ehrReviewTypeLines(
  selected: readonly string[],
  capabilities: readonly EhrWriteCapability[],
  sourceIsTabular: boolean,
  switches: EhrWriteKindSwitches,
): EhrReviewTypeLine[] {
  return selected.map((resourceType) => ({
    resourceType,
    kinds: kindsFor(resourceType, capabilities, sourceIsTabular)
      .filter((k) => isKindOn(k, switches))
      .map((k) => k.label),
  }));
}
