import { CanvasNode, isSourceNode } from '../../models/node.model';
import { EHR_VENDOR_TO_SOURCE_FORM_KEY } from './source-form.registry';

/** Best-effort vendor/source-type detection for an existing canvas source node — extracted from
 *  node-library-dialog.component.ts (originally `_sourceFormKeyForNode`/`_isGenericFhirNode`/
 *  `_isHl7v2Node`) so any other permission-aware consumer (e.g. canvas.component.ts's node-delete
 *  gate) resolves a node's vendor the exact same way, rather than re-deriving it independently and
 *  risking the two disagreeing. Reads the same 'Connector' field value every source form's own
 *  getFields() writes (see EhrVendorSourceFormComponent.buildFieldsToSave); a node saved before that
 *  field existed (any pre-refactor Epic node) falls back to 'epic', preserving prior behavior exactly.
 *
 *  Returns a sources.data.ts id (e.g. 'epic', 'cerner', 'generic-fhir', 'hl7v2') — the same id space
 *  `permissionPrefix` lookups and SOURCE_FORM_REGISTRY keys already use. */
export function sourceFormKeyForNode(node: CanvasNode): string {
  if (isGenericFhirNode(node)) return 'generic-fhir';
  if (isHl7v2Node(node)) return 'hl7v2';
  const connector = isSourceNode(node) ? node.fields['Connector'] : undefined;
  const mapped = connector ? EHR_VENDOR_TO_SOURCE_FORM_KEY[connector] : undefined;
  return mapped ?? 'epic';
}

/** Hl7v2SourceFormComponent.getFields() writes 'Connector': 'HL7 v2 / MLLP' — a human display string, not the
 *  bare 'Hl7v2' enum-name literal EHR_VENDOR_TO_SOURCE_FORM_KEY's other vendors use — so detecting it can't go
 *  through that map; regex on the display string is the only option. Exported so WorkflowBuildAssemblerService
 *  can recognize (and explicitly reject, rather than silently mis-persist) an Hl7v2 node the exact same way this
 *  file already does, instead of a second, possibly-drifting copy of the pattern. */
export const HL7V2_CONNECTOR_PATTERN = /hl7\s*v?\s*2|mllp/i;

function isGenericFhirNode(node: CanvasNode): boolean {
  return isSourceNode(node) && /generic.?fhir/i.test(node.fields['Connector'] ?? node.connectorLabel ?? '');
}

function isHl7v2Node(node: CanvasNode): boolean {
  return isSourceNode(node) && HL7V2_CONNECTOR_PATTERN.test(node.fields['Connector'] ?? node.connectorLabel ?? '');
}
