import { CanvasNode, isSourceNode } from '../models/node-v2.model';

/** The one edge shape both the canvas store (from/to) and a build request (mapped to from/to) can offer. */
export interface UpstreamEdge {
  from: string;
  to: string;
}

/** Follows the FIRST inbound edge back from `startId` until it reaches a source — the walk the build assembler has
 *  always used to pair a destination with its source. Null when the chain ends without one. */
export function firstUpstreamSourceId(
  startId: string,
  edges: readonly UpstreamEdge[],
  isSource: (id: string) => boolean,
): string | null {
  let current: string | null = startId;
  const seen = new Set<string>();
  while (current && !seen.has(current)) {
    if (isSource(current)) return current;
    seen.add(current);
    const from: string | null = edges.find((e) => e.to === current)?.from ?? null;
    current = from;
  }
  return null;
}

/** Every source reached walking back over ALL inbound edges from `startId` (itself included when it is a source),
 *  nearest first. A merge fed by two sources yields both. */
export function upstreamSourceIds(
  startId: string,
  edges: readonly UpstreamEdge[],
  isSource: (id: string) => boolean,
): string[] {
  const found: string[] = [];
  const seen = new Set<string>();
  const queue = [startId];
  while (queue.length) {
    const id = queue.shift()!;
    if (seen.has(id)) continue;
    seen.add(id);
    if (isSource(id)) {
      found.push(id);
      continue;
    }
    for (const edge of edges) {
      if (edge.to === id && !seen.has(edge.from)) queue.push(edge.from);
    }
  }
  return found;
}

/** The source nodes upstream of a canvas node (itself included when it is a source). */
export function upstreamSourceNodes(
  startId: string,
  nodes: readonly CanvasNode[],
  edges: readonly UpstreamEdge[],
): CanvasNode[] {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const isSource = (id: string): boolean => {
    const node = byId.get(id);
    return !!node && isSourceNode(node);
  };
  return upstreamSourceIds(startId, edges, isSource).map((id) => byId.get(id)!);
}

/** Marks a source node whose 'Resources' is the admin's own "Resource types to read" choice. Written by the source
 *  forms' pickers (EHR, Generic FHIR) whenever they save a declared list. A node without it was saved before sources
 *  declared their types: its 'Resources' / 'Retrieval resource type' may hold a silent default the form filled in
 *  (every supported type, the Generic FHIR form's 12 types, a cloned connection's list), never something the admin
 *  chose, so it is legacy — its fetch and scopes are still decided by its connection and its destinations. */
export const SOURCE_TYPES_DECLARED_KEY = 'Resource types declared';

/** Whether a source node's saved fields carry the declared-types marker (SOURCE_TYPES_DECLARED_KEY): 'true' in any
 *  letter case. A stored JSON boolean true counts too (the graph mapper's parseConfig turns it
 *  into the string 'true'; a raw boolean is accepted as well). Same rule as the server's WorkflowNodeResourceTypes.HasDeclaredTypesMarker. */
export function hasDeclaredTypesMarker(fields: Record<string, string> | null | undefined): boolean {
  const marker: unknown = fields?.[SOURCE_TYPES_DECLARED_KEY];
  return marker === true || (typeof marker === 'string' && marker.toLowerCase() === 'true');
}

/** De-duplicated case-insensitively, first spelling and order kept (the server's OrdinalIgnoreCase Distinct). */
const distinctIgnoreCase = (types: readonly string[]): string[] => {
  const seen = new Set<string>();
  return types.filter((t) => {
    const key = t.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
};

const splitList = (raw: string | undefined): string[] =>
  distinctIgnoreCase(
    (raw ?? '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
  );

/** A JSON array setting, whether it arrived as a JSON string (the field bag's usual shape) or as the array itself. */
const jsonArrayOf = (raw: unknown): unknown[] => {
  if (Array.isArray(raw)) return raw;
  if (typeof raw !== 'string' || !raw.trim()) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
};

const stringProp = (value: unknown, key: string): string | null => {
  if (!value || typeof value !== 'object') return null;
  const prop = (value as Record<string, unknown>)[key];
  return typeof prop === 'string' ? prop : null;
};

/** The types of a CSV / SQL Table node's tab_streams or legacy tab_templates: tab_streams names the type on the
 *  entry, a legacy tab_templates entry on its template (where TabularFhirTemplateEngine reads it from). */
const tabularResourceTypes = (raw: unknown): string[] =>
  distinctIgnoreCase(
    jsonArrayOf(raw)
      .map((entry) => {
        const direct = stringProp(entry, 'resourceType');
        if (direct !== null) return direct;
        const template = entry && typeof entry === 'object' ? (entry as Record<string, unknown>)['template'] : null;
        return stringProp(template, 'resourceType') ?? '';
      })
      .map((t) => t.trim())
      .filter(Boolean),
  );

/** A mapping row's "resource" counts only when it looks like a FHIR type (the server's LooksLikeResourceType):
 *  a blank or a display label is not a type the source could be asked to read. */
const looksLikeResourceType = (value: string): boolean => /^[A-Z][A-Za-z]*$/.test(value);

/** The resource types a source node declares it reads ("Resource types to read"), or null when it declares none —
 *  a legacy node whose fetch is still decided the old way (connection Retrieval, scopes, destinations). A CSV / SQL
 *  Table node declares them per stream (tab_streams, else the older tab_templates, else its 'Resources'); every
 *  other source in 'Resources' only, and only when the node carries SOURCE_TYPES_DECLARED_KEY. 'Retrieval resource
 *  type' is never read as a declared list (the form pre-filled it silently). Mirrors the server's
 *  WorkflowNodeResourceTypes.ReadSourceDeclared, plus the marker. */
export function declaredSourceResourceTypes(fields: Record<string, string> | null | undefined): string[] | null {
  if (!fields) return null;
  let types: string[] = [];
  if (fields['tab_kind']) {
    types = tabularResourceTypes(fields['tab_streams']);
    if (types.length === 0) types = tabularResourceTypes(fields['tab_templates']);
    if (types.length === 0) types = splitList(fields['Resources']);
  } else if (hasDeclaredTypesMarker(fields)) {
    types = splitList(fields['Resources']);
  }
  return types.length ? types : null;
}

/** What a destination writes: its Data groups (dest_resources) plus every resource its field mappings name. */
export function destinationWrittenResourceTypes(fields: Record<string, string> | null | undefined): string[] {
  if (!fields) return [];
  const mapped = jsonArrayOf(fields['dest_mappings'])
    .map((row) => stringProp(row, 'resource') ?? '')
    .filter(looksLikeResourceType);
  return distinctIgnoreCase([...splitList(fields['dest_resources']), ...mapped]);
}

/** The union of what the given sources declare, or null when any of them is a legacy source with no list (nothing
 *  to narrow by: the destination keeps today's choices). Null for no sources at all. */
export function declaredTypesOfSources(sources: readonly CanvasNode[]): string[] | null {
  if (sources.length === 0) return null;
  const union: string[] = [];
  for (const source of sources) {
    const declared = declaredSourceResourceTypes(source.fields);
    if (!declared) return null;
    union.push(...declared);
  }
  return distinctIgnoreCase(union);
}

/** A destination that writes types its source(s) do not read. */
export interface SourceTypeMismatch {
  destinationId: string;
  types: string[];
  message: string;
}

function isDestinationNode(node: CanvasNode): boolean {
  return node.kind === 'transform' && node.transformId.startsWith('dest-');
}

function quotedList(names: string[]): string {
  const quoted = names.map((n) => `'${n}'`);
  return quoted.length <= 1 ? quoted.join('') : `${quoted.slice(0, -1).join(', ')} and ${quoted[quoted.length - 1]}`;
}

/** How a node is named in the message when the caller has nothing better: its saved name, else its id (the
 *  server's fallback). The builder passes the graph mapper's displayName, which is what the server names it by. */
const defaultNodeName = (node: CanvasNode): string => node.fields['__name']?.trim() || node.id;

/** The save-time rule the server also applies (WorkflowResourceTypeSubsetRule): every type a destination writes must
 *  be one its upstream source(s) read, compared case-insensitively. Skipped for a destination fed by a legacy source
 *  with no declared list. Returns the first offender, worded as the server words it — `nameOf` should give the
 *  node's saved displayName (WorkflowGraphMapperServiceV2.savedDisplayName) so both name a node the same way. */
export function findDestinationTypesOutsideSource(
  nodes: readonly CanvasNode[],
  edges: readonly UpstreamEdge[],
  nameOf: (node: CanvasNode) => string = defaultNodeName,
): SourceTypeMismatch | null {
  for (const destination of nodes.filter(isDestinationNode)) {
    const written = destinationWrittenResourceTypes(destination.fields);
    if (written.length === 0) continue;
    const sources = upstreamSourceNodes(destination.id, nodes, edges);
    const declared = declaredTypesOfSources(sources);
    if (!declared) continue;
    const allowed = new Set(declared.map((t) => t.toLowerCase()));
    const outside = written.filter((t) => !allowed.has(t.toLowerCase()));
    if (outside.length === 0) continue;
    const destinationName = nameOf(destination);
    const sourceNames = sources.map(nameOf);
    const sourcePart = sourceNames.length === 1
      ? `its source ${quotedList(sourceNames)} does not read`
      : `its sources ${quotedList(sourceNames)} do not read`;
    return {
      destinationId: destination.id,
      types: outside,
      message:
        `Destination '${destinationName}' writes ${outside.join(', ')}, which ${sourcePart}. ` +
        `Add ${outside.length === 1 ? 'it' : 'them'} to the source's resource types or remove ` +
        `${outside.length === 1 ? 'it' : 'them'} from the destination.`,
    };
  }
  return null;
}
