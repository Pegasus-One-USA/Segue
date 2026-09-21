import { SOURCES } from './sources-v2.data';
import { TRANSFORMS } from './transforms-v2.data';
import { EHR_VENDOR_TO_SOURCE_FORM_KEY } from '../components/node-library/source-form.registry';

/**
 * Display names for the Source/Destination *type* enums, read off the very catalogs the Settings masters render
 * their own dropdowns from — SOURCES for Source Connections' "All EHRs" list, TRANSFORMS for Destination
 * Connections' grouped "All types" list.
 *
 * The Workflows list and Execution History previously derived their filter labels by splitting the raw enum name
 * on case boundaries, which produced names that exist nowhere else in the product: `DataFabricAzure` read as
 * "Data Fabric Azure" (the masters call it "OneLake Files"), `FhirRepository` as "Fhir Repository" ("Aidbox"),
 * `Mongo` as "Mongo" ("MongoDB"), and `Healow` as "Healow" ("eClinicalWorks"). Two screens naming the same
 * connection differently is the bug those labels caused; deriving both from the catalog is what stops it
 * recurring, since a vendor added to the catalog now reaches every screen at once.
 *
 * Only the *label* comes from here. The filter VALUE stays the enum member the API filters on.
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
