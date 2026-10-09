import type { WorkflowNodeDto } from './workflow-api.service';
import type { CanvasNode } from '../models/node-v2.model';
import { runModeOf, savedWriteVendorOf, vendorLabel } from '../components/node-library-v2/destination-wizard/destination-forms/ehr-write-back/ehr-write-back.model';

/** The transform every EHR Write-Back destination node is saved under, whichever EHR tile it was added from. */
export const EHR_WRITE_BACK_TRANSFORM_ID = 'dest-ehr-writeback';

/** One live EHR write in a workflow: the write-back node, the EHR it writes into and the connection it writes
 *  through. A test run and a dry run are not live writes; a plain FHIR server written to for real is. */
export interface LiveEhrWriteTarget {
  nodeId: string;
  vendor: string;
  connectionId: string;
}

/** The live EHR write a node's saved fields describe, or null when the node is not a live EHR Write-Back. */
export function liveEhrWriteTargetOf(
  nodeId: string,
  transformId: string | null | undefined,
  fields: Readonly<Record<string, string | undefined>>,
): LiveEhrWriteTarget | null {
  if (transformId !== EHR_WRITE_BACK_TRANSFORM_ID || runModeOf(fields) !== 'live') return null;
  const vendor = savedWriteVendorOf(fields);
  return vendor ? { nodeId, vendor, connectionId: fields['dest_sourceConnectionId'] ?? '' } : null;
}

/** The live EHR writes in a saved workflow definition. A node whose configuration cannot be read is skipped. */
export function liveEhrWriteTargetsOfDefinition(nodes: readonly Pick<WorkflowNodeDto, 'id' | 'configurationJson' | 'isEnabled'>[] | null | undefined): LiveEhrWriteTarget[] {
  const targets: LiveEhrWriteTarget[] = [];
  for (const node of nodes ?? []) {
    if (node.isEnabled === false) continue;
    let fields: Record<string, string>;
    try {
      const parsed: unknown = JSON.parse(node.configurationJson || '{}');
      if (!parsed || typeof parsed !== 'object') continue;
      fields = parsed as Record<string, string>;
    } catch {
      continue;
    }
    const target = liveEhrWriteTargetOf(node.id, fields['__transformId'], fields);
    if (target) targets.push(target);
  }
  return targets;
}

/** The live EHR writes on the workflow builder's canvas, as they would be saved. */
export function liveEhrWriteTargetsOfCanvas(nodes: readonly CanvasNode[]): LiveEhrWriteTarget[] {
  const targets: LiveEhrWriteTarget[] = [];
  for (const node of nodes) {
    if (node.kind !== 'transform') continue;
    const target = liveEhrWriteTargetOf(node.id, node.transformId, node.fields ?? {});
    if (target) targets.push(target);
  }
  return targets;
}

/** The live EHR writes in `after` that `before` did not have: a new live node, a node switched to live from a test
 *  or dry run, or a live node now writing into another EHR or through another connection. */
export function addedLiveEhrWriteTargets(
  before: readonly LiveEhrWriteTarget[],
  after: readonly LiveEhrWriteTarget[],
): LiveEhrWriteTarget[] {
  const keyOf = (target: LiveEhrWriteTarget) => `${target.nodeId}|${target.vendor}|${target.connectionId}`;
  const known = new Set(before.map(keyOf));
  return after.filter(target => !known.has(keyOf(target)));
}

/** The EHRs' own names, each once, in order: "Epic", "Epic and athenahealth", "Epic, eClinicalWorks and athenahealth". */
export function ehrNamesOf(vendors: readonly string[]): string {
  const labels: string[] = [];
  for (const vendor of vendors) {
    const label = vendorLabel(vendor);
    if (label && !labels.includes(label)) labels.push(label);
  }
  if (labels.length <= 1) return labels[0] ?? '';
  return `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;
}
