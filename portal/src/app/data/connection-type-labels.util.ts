import { SOURCES } from './sources-v2.data';
import { TRANSFORMS } from './transforms-v2.data';
import { SUPPORTED_RESOURCE_TYPES } from './scope-constants-v2.data';
import { EHR_VENDOR_TO_SOURCE_FORM_KEY } from '../components/node-library/source-form.registry';

/**
 * The Source/Destination/Resource Type vocabularies, read off the very catalogs the connection masters and the
 * destination wizard build their own pickers from — SOURCES for Source Connections' "All EHRs" dropdown,
 * TRANSFORMS for Destination Connections' grouped "All types" dropdown, and SUPPORTED_RESOURCE_TYPES for the
 * destination wizard's data-group picker.
 *
 * Both the LABELS and the OPTION LISTS come from here, because the list screens' filters were getting each of
 * them wrong in a different way:
 *
 *  * Labels were derived by splitting the raw enum on case boundaries, producing names used nowhere else in the
 *    product — `DataFabricAzure` read as "Data Fabric Azure" where the master says "OneLake Files".
 *  * Option lists were derived from the rows that happen to exist, so the filters offered only what was already
 *    in use (two EHR vendors, five destination types, five resource types) while the masters offer everything
 *    that can be chosen. A filter is a question about the catalog, not a summary of current data.
 *
 * Deriving both from the catalog is what stops either recurring: a vendor or destination added to a catalog
 * reaches the masters, the builder and these filters at once, with no second table to update.
 *
 * Only the label is presentational. The filter VALUE stays the enum member the API filters on.
 */

/** SourceSystemType enum name -> the SOURCES catalog entry that owns it (via the vendor->catalog-id map). */
const SOURCE_LABEL_BY_ENUM: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(EHR_VENDOR_TO_SOURCE_FORM_KEY)
    .map(([vendor, catalogId]) => [vendor, SOURCES.find(s => s.id === catalogId)?.name])
    .filter((entry): entry is [string, string] => !!entry[1]),
);

/** DestinationType enum name -> the TRANSFORMS catalog entry that declares it. */
const DESTINATION_LABEL_BY_ENUM: Readonly<Record<string, string>> = Object.fromEntries(
  TRANSFORMS
    .filter(t => !!t.destinationType)
    .map(t => [t.destinationType as string, t.name]),
);

/** Reverse of EHR_VENDOR_TO_SOURCE_FORM_KEY — SOURCES catalog id -> backend SourceSystemType name. */
const EHR_VENDOR_BY_SOURCE_KEY: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(EHR_VENDOR_TO_SOURCE_FORM_KEY).map(([vendor, key]) => [key, vendor]),
);

/** One filter checkbox: the enum the API filters on, plus the name the masters show for it. */
export interface ConnectionTypeOption {
  value: string;
  label: string;
}

/** Whether a catalog entry is enabled in the active phase config and permitted for the current role — the two
 *  gates the masters apply to their own dropdowns, passed in so this stays a pure data module. */
export interface CatalogGate {
  isEnabled: (catalogId: string) => boolean;
  hasPermission: (code: string) => boolean;
}

/**
 * Source filter options — every SOURCES vendor that maps to a backend SourceSystemType, gated exactly as
 * SourceConnectionListComponent.ehrOptions gates its "All EHRs" dropdown: hidden unless enabled in the active
 * phase config, and unless the role holds the vendor's own `{prefix}.view`.
 */
export function sourceTypeOptions(gate: CatalogGate): ConnectionTypeOption[] {
  return SOURCES
    .filter(s => gate.isEnabled(s.id) && (!s.permissionPrefix || gate.hasPermission(`${s.permissionPrefix}.view`)))
    .map(s => ({ value: EHR_VENDOR_BY_SOURCE_KEY[s.id], label: s.name }))
    .filter((o): o is ConnectionTypeOption => !!o.value);
}

/**
 * Destination filter options — every TRANSFORMS entry that declares a destinationType, gated exactly as
 * DestinationConnectionListComponent.buildTypeGroups gates its "All types" dropdown. Returned flat rather than
 * grouped: the filter is a checkbox panel with its own search box, not a native `<select>` with `<optgroup>`s.
 */
export function destinationTypeOptions(gate: CatalogGate): ConnectionTypeOption[] {
  const seen = new Set<string>();
  const options: ConnectionTypeOption[] = [];
  for (const t of TRANSFORMS) {
    if (!t.destinationType) continue;
    if (!gate.isEnabled(t.id)) continue;
    if (t.permissionPrefix && !gate.hasPermission(`${t.permissionPrefix}.view`)) continue;
    // Two catalog entries can share a destinationType (a grouped vendor's variants); the filter needs one box.
    if (seen.has(t.destinationType)) continue;
    seen.add(t.destinationType);
    options.push({ value: t.destinationType, label: t.name });
  }
  return options;
}

/**
 * Resource Type filter options — the same list the destination wizard's data-group picker offers, which is what
 * a workflow's `dest_resources` selection is made from. Not gated: resource types carry no phase config or
 * permission of their own, unlike vendors and destinations.
 */
export function resourceTypeOptions(): ConnectionTypeOption[] {
  return [...SUPPORTED_RESOURCE_TYPES]
    .sort((a, b) => a.localeCompare(b))
    .map(value => ({ value, label: value }));
}

/**
 * The name the Source Connections master shows for a `SourceSystemType` (e.g. `Healow` -> "eClinicalWorks").
 * An enum the catalog doesn't cover falls back to the raw value rather than rendering blank.
 */
export function sourceTypeLabel(sourceSystemType: string | null | undefined): string {
  if (!sourceSystemType) return '';
  return SOURCE_LABEL_BY_ENUM[sourceSystemType] ?? sourceSystemType;
}

/**
 * The name the Destination Connections master shows for a `DestinationType` (e.g. `DataFabricWarehouse` ->
 * "Warehouse"). Falls back to the raw enum for a type with no catalog entry — one that exists on the backend but
 * is not offered in the builder, which should therefore never appear as a filter option anyway.
 */
export function destinationTypeLabel(destinationType: string | null | undefined): string {
  if (!destinationType) return '';
  return DESTINATION_LABEL_BY_ENUM[destinationType] ?? destinationType;
}
