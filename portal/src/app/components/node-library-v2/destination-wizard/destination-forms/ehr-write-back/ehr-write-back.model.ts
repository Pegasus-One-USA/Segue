import { EhrWriteCapability } from '../../../../../services/ehr-write-capabilities.service';
import { EhrWriteVendor, WRITE_VENDOR_LABELS, isEhrWriteVendor } from '../../../../../connections/ehr-write-vendors';

/** The EHRs an EHR Write-Back destination writes to: declared once, with the write connections' vendors. */
export type { EhrWriteVendor };
export { isEhrWriteVendor };

/**
 * How a write-back run treats the records it resolves:
 * - live: writes into the vendor over its own write connection;
 * - dryRun: checks every record and sends nothing;
 * - test: sends exactly what the vendor would get to a FHIR test server (a Generic FHIR write connection) instead.
 * Saved as dest_dryRun + dest_testAsVendor; there is no run-mode key of its own.
 */
export type EhrRunMode = 'live' | 'dryRun' | 'test';

/** A write connection the destination can write through, with what its vendor accepts. */
export interface WritableTarget {
  id: string;
  name: string;
  vendor: string;
  resourceTypes: string[];
  /** Types a run that is not a dry run sends when selected: every type the code supports live for this vendor, over
   *  this connection (types needing vendor activation count only when the connection has it). */
  liveTypes: string[];
  /** Types that would be live once the connection's vendor write APIs are activated. */
  awaitingActivationTypes: string[];
  /** Some types are filed on an encounter the bridge creates (eCW medical/surgical history). */
  offersHolderEncounter: boolean;
  /** The QA-only clone-mode system setting is on, so clone mode may be offered. */
  cloneModeEnabled: boolean;
  /** Vendors this connection can stand in for in a test run (a Generic FHIR test server); empty otherwise. */
  testableVendors: string[];
  /** Variants a destination can turn on (a kind ticked on Step 2), one entry per variant; getFullConfig saves only
   *  these. */
  optInApis: OptInApi[];
  /** athenahealth: the connection's default department; this node's own Department id, when set, overrides it. */
  departmentId: string | null;
  /** Everything the vendor accepts. */
  capabilities: EhrWriteCapability[];
}

export interface OptInApi {
  variant: string;
  label: string;
  /** Needs the target's own ids, so only a CSV / SQL Table source can feed it. */
  tabularOnly: boolean;
}

/** The vendors a FHIR test server can stand in for. */
export const TESTABLE_VENDORS: readonly EhrWriteVendor[] = ['Epic', 'Healow', 'Athenahealth'];

export function isTestableVendor(v: unknown): v is EhrWriteVendor {
  return typeof v === 'string' && (TESTABLE_VENDORS as readonly string[]).includes(v);
}

/** How a vendor reads to an admin (eClinicalWorks, not Healow); any other value as it is. */
export function vendorLabel(v: string | null | undefined): string {
  return (isEhrWriteVendor(v) && WRITE_VENDOR_LABELS[v]) || v || '';
}

/**
 * The vendor a saved write-back writes to: the vendor a test run stands in for when it names one that can be tested,
 * else its own dest_ehrVendor; null when neither names a write vendor. dest_testAsVendor is read first because a test
 * run saved before its tested vendor's capabilities loaded recorded the test server's own vendor (GenericFhir) as
 * dest_ehrVendor. The Node Library tile, the canvas badge, the wizard's Step 2 types and its "copy settings from" list
 * all use this one rule.
 */
export function savedWriteVendorOf(fields: Readonly<Record<string, string | undefined>>): EhrWriteVendor | null {
  const testAs = fields['dest_testAsVendor'];
  if (isTestableVendor(testAs)) return testAs;
  const own = fields['dest_ehrVendor'];
  return isEhrWriteVendor(own) ? own : null;
}

/** savedWriteVendorOf for a saved destination's connectionMetadataJson; null when it cannot be read. The wizard's
 *  "copy settings from" list and the Destination Connections Type column both read a saved row through this. */
export function savedWriteVendorOfMetadata(metadataJson: string | null | undefined): EhrWriteVendor | null {
  try {
    const fields: unknown = JSON.parse(metadataJson || '{}');
    return fields && typeof fields === 'object' ? savedWriteVendorOf(fields as Record<string, string | undefined>) : null;
  } catch {
    return null;
  }
}

/**
 * The run mode saved fields describe. Anything but an explicit dest_dryRun 'false' is a dry run, as in the executor;
 * a dry run over a test server (dest_testAsVendor set) is still a dry run.
 */
export function runModeOf(fields: Readonly<Record<string, string | undefined>>): EhrRunMode {
  if (fields['dest_dryRun'] !== 'false') return 'dryRun';
  return isTestableVendor(fields['dest_testAsVendor']) ? 'test' : 'live';
}

/** The connections offered for an EHR, in the two groups the connection dropdown shows: the EHR's own write
 *  connections (a live run), and the FHIR test servers that receive exactly what it would. */
export function connectionGroupsFor(
  targets: readonly WritableTarget[],
  vendor: EhrWriteVendor,
): { own: WritableTarget[]; testServers: WritableTarget[] } {
  return {
    own: targets.filter(t => t.vendor === vendor),
    testServers: isTestableVendor(vendor)
      ? targets.filter(t => t.vendor === 'GenericFhir' && t.testableVendors.includes(vendor))
      : [],
  };
}

/** The Review step's lines for a write-back: where it writes and in which run mode. The mode is read back from the
 *  saved keys, exactly as a reopened node reads it. */
export function ehrReviewLines(
  config: Record<string, string>,
  vendor: string | null,
  connectionName: string | null,
): { writesTo: string; mode: string } {
  const connection = connectionName ?? 'the chosen connection';
  const label = vendorLabel(vendor ?? config['dest_ehrVendor']) || 'the EHR';
  const max = `, up to ${config['dest_maxWritesPerRun'] || '500'} records per run`;
  const mode = runModeOf(config);
  const writesTo = isTestableVendor(config['dest_testAsVendor'])
    ? `${connection} (FHIR test server), shaped as ${label}`
    : `${connection} (${label})`;
  if (mode === 'test') return { writesTo, mode: `Test run: nothing reaches ${label}${max}` };
  return {
    writesTo,
    mode: mode === 'live' ? `Live: writes into ${label}${max}` : `Dry run: checks every record, sends nothing${max}`,
  };
}
