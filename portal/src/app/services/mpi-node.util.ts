import { CanvasNode, isMpiNode } from '../models/node-v2.model';
import { MPI_IDENTIFIERS } from '../data/mpi-identifiers.data';

/** The MPI's catalog id (transforms-v2.data.ts) — also the `__transformId` its saved node carries, which is what
 *  tells it apart on load from a V1 'patient-matching' step sharing its PatientMatchingNode type. */
export const MPI_TRANSFORM_ID = 'mpi';

/** Node field holding the MPI's chosen identifier ids, comma-separated in MPI_IDENTIFIERS order. */
export const MPI_IDENTIFIERS_FIELD = 'mpiIdentifiers';

/** Config key on the SAVED MPI node holding everything set on the destination wizard's "MPI Rule" tab — a JSON
 *  array of MpiRule, one per (destination, resource) with at least one field chosen. Rebuilt on every save
 *  from the destinations' own configuration (see WorkflowGraphMapperServiceV2.mpiRules), so the canvas never
 *  edits it directly. */
export const MPI_RULES_FIELD = 'mpiRules';

/** Destination-node config key holding each resource's MpiThresholds, as a JSON object keyed by resource type. */
export const MPI_THRESHOLDS_FIELD = 'dest_mpiThresholds';

/**
 * How a record's match score (0–100%) against existing patients decides what happens to it:
 *   score ≥ autoApprove                 → auto-approve (treated as the same patient)
 *   manualReview ≤ score < autoApprove  → held for manual review
 *   score < manualReview                → no match (a new patient)
 * Null while the field is empty — never valid, see mpiThresholdErrors.
 */
export interface MpiThresholds {
  autoApprove: number | null;
  manualReview: number | null;
}

export const DEFAULT_MPI_THRESHOLDS: Readonly<MpiThresholds> = { autoApprove: 90, manualReview: 70 };

/** Problems with a pair of thresholds, by field — empty when it can be saved. */
export interface MpiThresholdErrors {
  autoApprove?: string;
  manualReview?: string;
}

const isPercent = (value: number | null): value is number =>
  value !== null && Number.isInteger(value) && value >= 0 && value <= 100;

export function mpiThresholdErrors(thresholds: MpiThresholds): MpiThresholdErrors {
  const errors: MpiThresholdErrors = {};
  const percentMessage = 'Enter a whole number from 0 to 100.';
  if (!isPercent(thresholds.autoApprove)) errors.autoApprove = percentMessage;
  if (!isPercent(thresholds.manualReview)) errors.manualReview = percentMessage;
  if (!errors.autoApprove && !errors.manualReview && thresholds.manualReview! >= thresholds.autoApprove!) {
    errors.manualReview = `Manual review must start below auto-approve (${thresholds.autoApprove}%).`;
  }
  return errors;
}

/** The saved thresholds for `resource` on a destination's config, or the defaults when none were set. */
export function mpiThresholdsFor(destinationFields: Record<string, string>, resource: string): MpiThresholds {
  try {
    const saved = JSON.parse(destinationFields[MPI_THRESHOLDS_FIELD] ?? '{}') as Record<string, MpiThresholds>;
    return saved?.[resource] ?? { ...DEFAULT_MPI_THRESHOLDS };
  } catch {
    return { ...DEFAULT_MPI_THRESHOLDS };
  }
}

/** One mapped field the MPI compares. */
export interface MpiRuleField {
  /** FHIR path(s) the value is read from — several for a joined field, a group id for a whole-node one. */
  sourceFields: string[];
  /** Where that value is written — the table and column an existing patient would be found in. */
  table: string;
  column: string;
}

/** What the MPI applies to one resource written to one destination: the fields it compares, and how the
 *  resulting score is acted on. */
export interface MpiRule {
  /** Stable key (canvasNodeId) of the destination node the rule was set on. */
  destination: string;
  resourceType: string;
  autoApprovePercent: number;
  manualReviewPercent: number;
  fields: MpiRuleField[];
}

export const MPI_NODE_NAME = 'Master Patient Index';

/** A workflow has at most one MPI node — the picker stops offering it once one exists. */
export function hasMpiNode(nodes: CanvasNode[]): boolean {
  return nodes.some(isMpiNode);
}

/** The node's selected identifier ids. Unknown ids (from a catalog entry since removed) are dropped. */
export function mpiIdentifierIds(node: CanvasNode): string[] {
  const raw = node.fields?.[MPI_IDENTIFIERS_FIELD] ?? '';
  const chosen = new Set(raw.split(',').map(id => id.trim()).filter(Boolean));
  return MPI_IDENTIFIERS.filter(identifier => chosen.has(identifier.id)).map(identifier => identifier.id);
}

/** Serializes a selection in catalog order, so the saved value doesn't depend on click order. */
export function serializeMpiIdentifiers(ids: Iterable<string>): string {
  const chosen = new Set(ids);
  return MPI_IDENTIFIERS.filter(identifier => chosen.has(identifier.id)).map(identifier => identifier.id).join(',');
}
