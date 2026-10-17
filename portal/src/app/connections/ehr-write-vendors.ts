import { EhrVendor } from '../ehr-endpoints/models/ehr-endpoint.model';
import { SourceConnectionModel } from '../source-connections/models/source-connection.model';
import { SOURCES } from '../data/sources.data';

/** The EHRs an EHR write connection (and an EHR Write-Back destination) writes to, as backend SourceSystemType names.
 *  The one declaration of the write vendors: the destination form, the Node Library tiles and the connection lists
 *  all import it. */
export type EhrWriteVendor = Extract<EhrVendor, 'Epic' | 'Healow' | 'Athenahealth' | 'GenericFhir'>;

/** An EHR an EHR write connection can be made for. Labels are what admins call them (eClinicalWorks, not the
 *  backend's Healow enum name). */
export interface WriteVendor {
  value: EhrWriteVendor;
  /** Its SOURCES catalog id (abbreviation, brand colour, permission prefix). */
  sourceId: string;
  label: string;
  sub: string;
  /** Overrides the SOURCES abbreviation where the write side reads differently (a plain FHIR server, not "R4"). */
  abbr?: string;
}

/** The vendors an EHR write connection can be created for, in card order. Shared by the Destination Connections
 *  list, the write-back destination form and the workflow's EHR destination tiles (names and badges). */
export const WRITE_VENDORS: readonly WriteVendor[] = [
  { value: 'Epic', sourceId: 'epic', label: 'Epic', sub: 'Epic write-back over a Backend System app.' },
  { value: 'Healow', sourceId: 'healow', label: 'eClinicalWorks', sub: 'eClinicalWorks (Healow) FHIR write APIs.' },
  { value: 'Athenahealth', sourceId: 'athena', label: 'athenahealth', sub: 'athenaOne write APIs.' },
  { value: 'GenericFhir', sourceId: 'generic-fhir', label: 'FHIR test server', sub: 'A FHIR R4 test server that receives exactly what an EHR would; nothing reaches the EHR.', abbr: 'FHR' },
];

/** How each write vendor reads to an admin. */
export const WRITE_VENDOR_LABELS: Readonly<Record<EhrWriteVendor, string>> =
  Object.fromEntries(WRITE_VENDORS.map(v => [v.value, v.label])) as Record<EhrWriteVendor, string>;

export function isEhrWriteVendor(v: unknown): v is EhrWriteVendor {
  return typeof v === 'string' && WRITE_VENDORS.some(w => w.value === v);
}

/** Vendors whose write-back types need contracted / proprietary APIs the practice must have activated. */
export const ACTIVATION_VENDORS: ReadonlySet<EhrVendor> = new Set<EhrVendor>(['Healow', 'Athenahealth']);

/** A write vendor with its catalog look and its own permission prefix (`{prefix}.create` and so on). */
export interface WriteVendorCard extends WriteVendor {
  abbr: string;
  color: string;
  permissionPrefix: string;
}

export function writeVendorCards(): WriteVendorCard[] {
  return WRITE_VENDORS.map(v => {
    const entry = SOURCES.find(s => s.id === v.sourceId);
    return {
      ...v,
      abbr: v.abbr ?? entry?.abbr ?? v.label.slice(0, 2).toUpperCase(),
      color: entry?.color ?? 'var(--color-primary)',
      permissionPrefix: entry?.permissionPrefix ?? v.value.toLowerCase(),
    };
  });
}

/** The card for one write vendor, or null for a vendor that cannot be written to. */
export function writeVendorCard(vendor: string | null | undefined): WriteVendorCard | null {
  return writeVendorCards().find(card => card.value === vendor) ?? null;
}

/** "Activated" / "Not activated" for eCW and athena; null where the vendor has no activation-gated APIs. */
export function writeApisLabel(c: Pick<SourceConnectionModel, 'sourceSystemType' | 'vendorWriteApisActivated'>): string | null {
  if (!ACTIVATION_VENDORS.has(c.sourceSystemType)) return null;
  return c.vendorWriteApisActivated ? 'Activated' : 'Not activated';
}
