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
 * Saved as dest_dryRun + dest_testAsVendor (runModeFields); there is no run-mode key of its own.
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
  /** APIs the destination must enable on top of selecting the type, one entry per variant. */
  optInApis: OptInApi[];
  /** athenahealth: the connection's default department; this node's own Department id, when set, overrides it. */
  departmentId: string | null;
  /** Everything the vendor accepts, so the opt-ins a selection still needs can be worked out (missingOptInsFor). */
  capabilities: EhrWriteCapability[];
}

export interface OptInApi {
  variant: string;
  label: string;
  /** Needs the target's own ids, so only a CSV / SQL Table source can feed it. */
  tabularOnly: boolean;
}

/** A selected resource type the writer would skip until an option is turned on under Options. */
export interface EhrMissingOptIn {
  resourceType: string;
  /** API variants any one of which, ticked, lets the type be written. */
  variants: string[];
  /** The type is written only on a holder encounter (eCW history). */
  holderEncounter: boolean;
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

/** The run modes a vendor offers: a FHIR server (or a vendor not known yet) has no test server to stand in for. */
export function runModesFor(vendor: EhrWriteVendor | null): EhrRunMode[] {
  return vendor === null || vendor === 'GenericFhir' ? ['live', 'dryRun'] : ['live', 'dryRun', 'test'];
}

/**
 * The run mode saved fields describe. Anything but an explicit dest_dryRun 'false' is a dry run, as in the executor.
 * A saved test run that was also a dry run (the old "Test as" plus "Dry run") reopens as a plain dry run, and
 * `droppedTestServer` says the test server it pointed at no longer fits.
 */
export function runModeOf(fields: Record<string, string>): { mode: EhrRunMode; droppedTestServer: boolean } {
  const testAs = isTestableVendor(fields['dest_testAsVendor']);
  const dry = fields['dest_dryRun'] !== 'false';
  if (testAs && !dry) return { mode: 'test', droppedTestServer: false };
  if (testAs && dry) return { mode: 'dryRun', droppedTestServer: true };
  return { mode: dry ? 'dryRun' : 'live', droppedTestServer: false };
}

/** The saved fields for a run mode. Test needs a testable vendor; callers gate on the form being valid first. */
export function runModeFields(
  mode: EhrRunMode,
  vendor: EhrWriteVendor | null,
): { dest_dryRun: 'true' | 'false'; dest_testAsVendor: string } {
  if (mode === 'live') return { dest_dryRun: 'false', dest_testAsVendor: '' };
  if (mode === 'dryRun') return { dest_dryRun: 'true', dest_testAsVendor: '' };
  if (!isTestableVendor(vendor)) throw new Error(`A test run cannot stand in for ${vendor ?? 'no vendor'}.`);
  return { dest_dryRun: 'false', dest_testAsVendor: vendor };
}

/**
 * The write connections a run mode can use: the vendor's own for Live and Dry run, and FHIR servers that can stand in
 * for it in a test run. With no vendor known yet, every connection is offered.
 */
export function connectionsFor(targets: WritableTarget[], vendor: EhrWriteVendor | null, mode: EhrRunMode): WritableTarget[] {
  if (vendor === null) return targets;
  if (mode === 'test') return targets.filter(t => t.vendor === 'GenericFhir' && t.testableVendors.includes(vendor));
  return targets.filter(t => t.vendor === vendor);
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
  const { mode } = runModeOf(config);
  if (mode === 'test') {
    return {
      writesTo: `${connection} (FHIR test server), shaped as ${label}`,
      mode: `Test on FHIR server: nothing reaches ${label}${max}`,
    };
  }
  return {
    writesTo: `${connection} (${label})`,
    mode: mode === 'live' ? `Live: writes into ${label}${max}` : `Dry run: checks every record, sends nothing${max}`,
  };
}
