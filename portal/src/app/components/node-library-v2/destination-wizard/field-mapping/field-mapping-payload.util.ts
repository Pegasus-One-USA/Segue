import type { ResourceFieldDef } from '../destination-wizard.component';

/**
 * Turns a pasted FHIR resource (or Bundle) JSON into the same ResourceFieldDef[] shape the real
 * backend catalog produces, so it flows through buildResourceTree/availableFields completely
 * unchanged — every nested object becomes a group, every array becomes a repeating group, mirroring
 * the pasted payload's own shape instead of the curated built-in field list.
 *
 * `arrays` only records ancestor *group* paths (matching the backend catalog's convention — see
 * EmbeddedFhirElementCatalog / FhirElement.arrays), never a leaf's own path, even when the leaf's
 * value is itself a repeating primitive (e.g. name.given) — the rest of this feature already treats
 * "arrays" this way for real-catalog fields, so payload-derived fields stay consistent with it.
 */

export type ParsePayloadResult =
  | { ok: true; fields: ResourceFieldDef[]; declaredResourceType?: string }
  | { ok: false; error: string };

export function parseSourcePayloadJson(resource: string, raw: string): ParsePayloadResult {
  const text = raw.trim();
  if (!text) return { ok: false, error: 'Paste a FHIR resource or Bundle JSON first.' };

  let payload: unknown;
  try {
    payload = JSON.parse(text);
  } catch {
    return { ok: false, error: 'That is not valid JSON — check for a missing brace, quote, or comma.' };
  }

  if (!isPlainObject(payload)) {
    return { ok: false, error: 'Paste a single FHIR resource object, or a Bundle.' };
  }

  const instances = extractInstances(payload, resource);
  if (!instances.length) {
    const found = Array.isArray(payload['entry'])
      ? [...new Set(
          (payload['entry'] as unknown[])
            .map(e => (isPlainObject(e) && isPlainObject(e['resource']) ? e['resource']['resourceType'] : undefined))
            .filter((t): t is string => typeof t === 'string'),
        )]
      : [];
    return {
      ok: false,
      error: found.length
        ? `That Bundle has no "${resource}" entries — it contains ${found.join(', ')} instead.`
        : `That Bundle has no resource entries to read.`,
    };
  }

  const merged = mergeShapes(instances);
  const fields: ResourceFieldDef[] = [];
  walk(merged, resource, '', [], fields);

  if (!fields.length) {
    return { ok: false, error: `The ${resource} resource in the pasted JSON has no fields.` };
  }

  const declared = typeof payload['resourceType'] === 'string' ? payload['resourceType'] : undefined;
  return {
    ok: true,
    fields,
    declaredResourceType: declared && declared !== resource && declared !== 'Bundle' ? declared : undefined,
  };
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === 'object' && !Array.isArray(v);
}

// ── Destination-side "Load JSON payload" (file-shaped destinations: CSV/Blob/Data Lake/Fabric/API
// Endpoint) — paste the target system's OWN expected body shape and get back BOTH a flat column list
// AND a ready-to-use Request Body Template (for the ApiEndpoint destination specifically — every other
// file-shaped type has no template concept and just ignores templateJson).
//
// The whole point of loading a payload here is that the user should never have to hand-write
// {{ColumnName}} placeholders themselves — MappedApiEndpointDestinationWriter.SubstituteInPlace only
// ever replaces text that already IS a {{placeholder}}, so a template built from the pasted example's
// OWN literal values (as-is) would send that same literal example back on every real call, which is
// exactly the confusing "it always sends the sample data" failure mode this exists to prevent. Instead
// buildTemplateNode below reconstructs the SAME nested shape the user pasted, but with every leaf value
// replaced by the exact {{ColumnName}} placeholder for that leaf — using the identical path→PascalCase
// naming pushLeaf uses for the source side, so a template placeholder always matches a real column name.
// Deliberately NOT resource/FHIR-aware like parseSourcePayloadJson above: this is an arbitrary partner
// JSON shape, not a FHIR resource, so there is no resourceType/Bundle special-casing and no per-field
// ResourceFieldDef — just names and a template, for a destination that only ever asked for those.
export type ParseDestinationPayloadResult =
  | { ok: true; columns: string[]; templateJson: string; note?: string }
  | { ok: false; error: string };

export function parseDestinationPayloadJson(raw: string): ParseDestinationPayloadResult {
  const text = raw.trim();
  if (!text) return { ok: false, error: 'Paste a sample JSON body first.' };

  let payload: unknown;
  try {
    payload = JSON.parse(text);
  } catch {
    return { ok: false, error: 'That is not valid JSON — check for a missing brace, quote, or comma.' };
  }

  if (!isPlainObject(payload)) {
    return { ok: false, error: 'Paste a single JSON object — the shape of one record your destination expects.' };
  }

  // A Request Body Template normally describes exactly ONE record — MappedApiEndpointDestinationWriter
  // applies it once per record, and it's the destination's own "Payload shape" setting (Envelope/JSON
  // array) that wraps N of those into a batch. Pasting an already-batched example (an envelope like
  // { meta: {...}, records: [...] }) as-is would build a template that puts an array — and that array's
  // own envelope fields — INSIDE the per-record loop, nesting a whole second envelope inside every record
  // instead of producing one.
  //
  // MappedApiEndpointDestinationWriter now understands exactly this shape, though: a template whose top
  // level is precisely { "meta": {...}, "records": [ <one record> ] } — with the destination's Payload
  // shape set to Envelope — has its "meta" substituted ONCE for the whole batch, against the batch's first
  // record's mapped values (every record in a batch shares one mapping profile/resource type, so the first
  // record's values are representative), and its "records[0]" substituted once per record. So when the
  // pasted payload has that literal shape, this PRESERVES it (rather than discarding "meta" the way an
  // unrecognized shape still has to) — every meta FIELD becomes a regular mappable column too (prefixed
  // "Meta" so it's visually distinct from the record columns on the same card, but dragged onto exactly
  // the same way), with the one exception of recordCount, which has no per-record source to drag from at
  // all (it's a fact about the whole batch) and is always filled in automatically instead.
  const metaEntry = isPlainObject(payload['meta']) ? (payload['meta'] as Record<string, unknown>) : null;
  const recordsEntry = Array.isArray(payload['records']) ? (payload['records'] as unknown[]) : null;
  const isRecognizedEnvelope = !!metaEntry && !!recordsEntry && recordsEntry.some(isPlainObject)
    && Object.keys(payload).length === 2;

  // Any OTHER shape with exactly one array-of-objects property (a different key than "records", or extra
  // sibling keys FHIRBridge's envelope substitution doesn't know how to place) still can't be sent as-is —
  // fall back to the older behavior: build the template from the array item's shape alone, and tell the
  // caller to pick Payload shape manually, same as before this envelope shape was supported.
  const arrayOfObjectsEntries = Object.entries(payload).filter(
    ([, v]) => Array.isArray(v) && v.some(isPlainObject));
  const isUnrecognizedBatchEnvelope = !isRecognizedEnvelope
    && arrayOfObjectsEntries.length === 1 && Object.keys(payload).length > 1;

  const columns: string[] = [];
  let note: string | undefined;
  let templateJson: string;

  if (isRecognizedEnvelope) {
    const recordShape = mergeShapes(recordsEntry!);
    const recordTemplate = buildTemplateNode(recordShape, '', columns);
    const metaTemplate = buildMetaTemplateNode(metaEntry!, columns);
    templateJson = JSON.stringify({ meta: metaTemplate, records: [recordTemplate] }, null, 2);
    note = `Detected a batch envelope ("meta"/"records") — meta fields became mappable "Meta…" columns `
      + `alongside your record columns (drag a source field onto them the same way); recordCount is filled `
      + `in automatically and isn't mappable. Set this destination's Payload shape (Configure step) to `
      + `"Envelope" to match.`;
  } else if (isUnrecognizedBatchEnvelope) {
    const [key, items] = arrayOfObjectsEntries[0];
    const recordShape = mergeShapes(items as unknown[]);
    const recordTemplate = buildTemplateNode(recordShape, '', columns);
    templateJson = JSON.stringify(recordTemplate, null, 2);
    note = `Detected a batch envelope ("${key}": [...]) — built the template from one record inside it, `
      + `not the envelope itself. Set this destination's Payload shape (Configure step) to "Envelope" or `
      + `"JSON array" to get that wrapping automatically; don't include it in the template yourself.`;
  } else {
    templateJson = JSON.stringify(buildTemplateNode(payload, '', columns), null, 2);
  }

  if (!columns.length) {
    return { ok: false, error: 'That JSON object has no fields.' };
  }

  return { ok: true, columns, templateJson, note };
}

/** Builds the "meta" half of an envelope template — every top-level field becomes a regular mappable
 *  column via buildTemplateNode, namespaced under "meta." (so e.g. "resourceType" becomes column
 *  "MetaResourceType", never colliding with a same-named record column) and dragged onto exactly like any
 *  other column. recordCount is the one exception: MappedApiEndpointDestinationWriter.BuildEnvelope fills
 *  it in from the real batch size, since there is no per-record field a user could ever drag onto it —
 *  offering it as a column would just be a column that can never resolve to anything. */
function buildMetaTemplateNode(metaEntry: Record<string, unknown>, columns: string[]): Record<string, unknown> {
  const obj: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(metaEntry)) {
    obj[key] = key.toLowerCase() === 'recordcount'
      ? '{{recordCount}}'
      : buildTemplateNode(value, `meta.${key}`, columns);
  }
  return obj;
}

/** Same recursion shape as walk() above (object → recurse, array of objects → merge shapes and recurse,
 *  array of primitives/leaf → one column) minus every FHIR-specific concern (no resourceType exclusion,
 *  no per-field label/valueType/arrays bookkeeping) — collects the flat column-name list into `columns`
 *  as a side effect while building the parallel template tree (every leaf's example value replaced by
 *  its own {{ColumnName}} placeholder, matching what ApiEndpointSender.SubstituteInPlace can actually
 *  substitute — see MappedApiEndpointDestinationWriter's exact-placeholder vs. embedded-placeholder
 *  handling). An array of objects keeps exactly one representative element (its merged shape) since the
 *  mapping model is flat columns → one scalar value per record, not per-array-item substitution. */
function buildTemplateNode(value: unknown, cum: string, columns: string[]): unknown {
  if (Array.isArray(value)) {
    if (!value.length) return [];
    const first = value.find(v => v !== null && v !== undefined);
    if (first === undefined) return [];
    if (isPlainObject(first)) {
      return [buildTemplateNode(mergeShapes(value), cum, columns)];
    }
    if (Array.isArray(first)) return []; // arrays of arrays have no meaningful shape to mirror — skipped.
    const column = pathToColumn(cum);
    columns.push(column);
    return [`{{${column}}}`];
  }

  if (isPlainObject(value)) {
    const obj: Record<string, unknown> = {};
    for (const [key, v] of Object.entries(value)) {
      obj[key] = buildTemplateNode(v, cum ? `${cum}.${key}` : key, columns);
    }
    return obj;
  }

  const column = pathToColumn(cum);
  columns.push(column);
  return `{{${column}}}`;
}

/** Matches pushLeaf's own column-naming convention exactly (path.split('.').map(capitalize).join('')),
 *  so a column loaded this way is indistinguishable from one the source-payload/catalog side would
 *  have produced for the same field name. */
function pathToColumn(path: string): string {
  return path.split('.').map(s => s.charAt(0).toUpperCase() + s.slice(1)).join('');
}

/**
 * A single (non-Bundle) object is accepted as-is regardless of its own `resourceType` — the "resource"
 * here is just this canvas's data-group name (Step 2's selection), not necessarily a literal FHIR
 * resourceType, since this same canvas also maps non-FHIR sources (HL7v2, flat file, DB). A Bundle is
 * the one case that genuinely needs disambiguation, since it can contain several different resource
 * types at once — there we still filter by resourceType to pick out the relevant entries.
 */
function extractInstances(payload: Record<string, unknown>, resource: string): Record<string, unknown>[] {
  if (payload['resourceType'] === 'Bundle' && Array.isArray(payload['entry'])) {
    return (payload['entry'] as unknown[])
      .map(e => (isPlainObject(e) ? e['resource'] : undefined))
      .filter((r): r is Record<string, unknown> => isPlainObject(r) && r['resourceType'] === resource);
  }
  return [payload];
}

/** Union of keys across every instance/array-item, so a field present on only one item is still captured. */
function mergeShapes(items: unknown[]): Record<string, unknown> {
  const merged: Record<string, unknown> = {};
  for (const item of items) {
    if (!isPlainObject(item)) continue;
    for (const [k, v] of Object.entries(item)) {
      if (merged[k] === undefined || merged[k] === null) merged[k] = v;
    }
  }
  return merged;
}

function walk(
  obj: Record<string, unknown>,
  resource: string,
  cum: string,
  arrayAncestors: string[],
  out: ResourceFieldDef[],
): void {
  for (const [key, value] of Object.entries(obj)) {
    // Never a useful mapping target — it's the same constant string on every instance of this
    // resource, redundant with the group/resource name itself. Excluded at every depth (e.g. also
    // inside `contained` resources), not just the root.
    if (key === 'resourceType') continue;

    const path = cum ? `${cum}.${key}` : key;

    if (Array.isArray(value)) {
      if (!value.length) continue;
      const first = value.find(v => v !== null && v !== undefined);
      if (first === undefined) continue;
      if (isPlainObject(first)) {
        walk(mergeShapes(value), resource, path, [...arrayAncestors, path], out);
      } else if (!Array.isArray(first)) {
        pushLeaf(out, resource, path, key, first, arrayAncestors);
      }
      // Arrays of arrays have no meaningful FHIR shape to mirror — skipped.
      continue;
    }

    if (isPlainObject(value)) {
      walk(value, resource, path, arrayAncestors, out);
      continue;
    }

    pushLeaf(out, resource, path, key, value, arrayAncestors);
  }
}

function pushLeaf(
  out: ResourceFieldDef[],
  resource: string,
  path: string,
  key: string,
  sampleValue: unknown,
  arrays: string[],
): void {
  const column = path.split('.').map(s => s.charAt(0).toUpperCase() + s.slice(1)).join('');
  out.push({
    label: humanize(key),
    path: `${resource}.${path}`,
    sqlColumn: column,
    csvColumn: column,
    jsonPath: path,
    valueType: inferValueType(sampleValue),
    arrays: arrays.length ? [...arrays] : undefined,
  });
}

function humanize(segment: string): string {
  const spaced = segment.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function inferValueType(value: unknown): string {
  if (typeof value === 'boolean') return 'Boolean';
  if (typeof value === 'number') return Number.isInteger(value) ? 'Integer' : 'Decimal';
  if (typeof value === 'string') {
    if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value)) return 'DateTime';
    if (/^\d{4}-\d{2}-\d{2}$/.test(value)) return 'Date';
  }
  return 'String';
}

// ── Reconstructing a resource's CURRENT payload back into JSON (Load JSON Payload modal's "original") ──
// The wizard never persists the literal raw JSON someone pastes — only the ResourceFieldDef[] it parses
// out of it (payloadFieldsByResource; see DestinationWizardComponent.onSourcePayloadLoaded), which is
// also exactly what already drives this resource's source tree (buildResourceTree, field-mapping-tree.
// util.ts) whether those fields came from a real pasted payload or the built-in/backend FHIR catalog. So
// rather than inventing a second, separate "original payload" concept, this rebuilds a JSON document
// straight from that SAME field list — the modal's textarea then always shows exactly the structure the
// source tree is already displaying for this resource, never a generic fixture unrelated to it.
//
// This is necessarily a reconstruction, not a byte-for-byte replay: ResourceFieldDef only ever carries a
// field's label/path/valueType/array-ancestry, never a sample value, so there is no original literal
// value anywhere in this app's model to recover — every leaf gets a type-appropriate placeholder instead.
// Structure, nesting, and field set are exact; only the literal values can't be.

/** Mirrors buildResourceTree's own fallback/segmenting rule exactly (same isFallback check, same
 *  relPath derivation) — this payload's nesting must match whatever the source tree is actually
 *  rendering for these same fields. */
export function reconstructPayloadJsonFor(resource: string, fields: ResourceFieldDef[]): string {
  const root: Record<string, unknown> = { resourceType: resource };
  if (!fields.length) return JSON.stringify(root, null, 2);

  const isFallback = fields.every(f => f.jsonPath === undefined);
  for (const f of fields) {
    const relPath = f.path.startsWith(`${resource}.`) ? f.path.slice(resource.length + 1) : f.path;
    const segments = isFallback ? [relPath] : relPath.split('.');
    placeValue(root, segments, new Set(f.arrays ?? []), placeholderValue(f));
  }
  return JSON.stringify(root, null, 2);
}

/** Descends/creates nested objects per `segments`, wrapping any segment whose cumulative dot-path is a
 *  known array-ancestor (ResourceFieldDef.arrays) in a single-element array — mirrors how a real FHIR
 *  payload nests a repeating group (e.g. `name: [{ family: ... }]`), just always exactly one instance.
 *  Reuses an already-built container for a path two different fields share (e.g. "name.family" and
 *  "name.given" both land inside the SAME "name" object/array-item), never overwriting a sibling. */
function placeValue(root: Record<string, unknown>, segments: string[], arrayAncestors: Set<string>, value: unknown): void {
  let node = root;
  let cum = '';
  for (let i = 0; i < segments.length; i++) {
    const seg = segments[i];
    cum = cum ? `${cum}.${seg}` : seg;
    if (i === segments.length - 1) {
      node[seg] = value;
      break;
    }
    if (arrayAncestors.has(cum)) {
      const existing = node[seg];
      const item: Record<string, unknown> = Array.isArray(existing) && isPlainObject(existing[0]) ? existing[0] : {};
      if (!Array.isArray(existing)) node[seg] = [item];
      node = item;
    } else {
      const existing = node[seg];
      const container: Record<string, unknown> = isPlainObject(existing) ? existing : {};
      node[seg] = container;
      node = container;
    }
  }
}

/** One representative value per ResourceFieldDef.valueType — see reconstructPayloadJsonFor's own doc
 *  comment on why this can only ever be a placeholder, never the field's true original value. */
function placeholderValue(f: ResourceFieldDef): unknown {
  switch (f.valueType) {
    case 'Boolean': return false;
    case 'Integer':
    case 'Decimal': return 0;
    case 'Date': return '2026-01-01';
    case 'DateTime': return '2026-01-01T00:00:00Z';
    default: return '';
  }
}

// ── Sample payloads for the modal's "Use sample FHIR {resource}" shortcut ──────────────────────────
// No longer wired into the Load JSON Payload modal itself (see reconstructPayloadJsonFor above, now used
// for both its initial fill and its "Reset to Original" button) — kept here since it's still exported/
// tested (field-mapping-payload.util.spec.ts) and may still be useful as a starting-point fixture
// elsewhere; nothing below this line was changed.
const SAMPLE_PAYLOADS: Record<string, unknown> = {
  Patient: {
    resourceType: 'Patient',
    id: 'example-patient-1',
    active: true,
    name: [{ use: 'official', family: 'Doe', given: ['Jane', 'Marie'] }],
    telecom: [
      { system: 'phone', value: '555-0100', use: 'home' },
      { system: 'email', value: 'jane.doe@example.com', use: 'work' },
    ],
    gender: 'female',
    birthDate: '1985-04-12',
    address: [{
      use: 'home', line: ['123 Main St'], city: 'Springfield', state: 'IL', postalCode: '62704', country: 'US',
    }],
    maritalStatus: {
      coding: [{ system: 'http://terminology.hl7.org/CodeSystem/v3-MaritalStatus', code: 'M', display: 'Married' }],
    },
    extension: [
      {
        url: 'http://hl7.org/fhir/us/core/StructureDefinition/us-core-race',
        extension: [
          { url: 'ombCategory', valueCoding: { system: 'urn:oid:2.16.840.1.113883.6.238', code: '2106-3', display: 'White' } },
          { url: 'text', valueString: 'White' },
        ],
      },
      {
        url: 'http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity',
        extension: [
          { url: 'ombCategory', valueCoding: { system: 'urn:oid:2.16.840.1.113883.6.238', code: '2186-5', display: 'Not Hispanic or Latino' } },
          { url: 'text', valueString: 'Not Hispanic or Latino' },
        ],
      },
    ],
  },
  Observation: {
    resourceType: 'Observation',
    id: 'example-observation-1',
    status: 'final',
    category: [{ coding: [{ system: 'http://terminology.hl7.org/CodeSystem/observation-category', code: 'vital-signs' }] }],
    code: { coding: [{ system: 'http://loinc.org', code: '8302-2', display: 'Body height' }] },
    subject: { reference: 'Patient/example-patient-1' },
    effectiveDateTime: '2026-06-01T09:30:00Z',
    valueQuantity: { value: 172, unit: 'cm', system: 'http://unitsofmeasure.org', code: 'cm' },
  },
  Encounter: {
    resourceType: 'Encounter',
    id: 'example-encounter-1',
    status: 'finished',
    class: { system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode', code: 'AMB', display: 'ambulatory' },
    type: [{ coding: [{ system: 'http://snomed.info/sct', code: '185349003', display: 'Encounter for check up' }] }],
    subject: { reference: 'Patient/example-patient-1' },
    period: { start: '2026-06-01T09:00:00Z', end: '2026-06-01T09:45:00Z' },
  },
  Condition: {
    resourceType: 'Condition',
    id: 'example-condition-1',
    clinicalStatus: { coding: [{ system: 'http://terminology.hl7.org/CodeSystem/condition-clinical', code: 'active' }] },
    code: { coding: [{ system: 'http://snomed.info/sct', code: '38341003', display: 'Hypertension' }] },
    subject: { reference: 'Patient/example-patient-1' },
    onsetDateTime: '2024-02-10',
    recordedDate: '2024-02-10',
  },
  MedicationRequest: {
    resourceType: 'MedicationRequest',
    id: 'example-medicationrequest-1',
    status: 'active',
    intent: 'order',
    medicationCodeableConcept: { coding: [{ system: 'http://www.nlm.nih.gov/research/umls/rxnorm', code: '197361', display: 'Amoxicillin 250mg' }] },
    subject: { reference: 'Patient/example-patient-1' },
    requester: { reference: 'Practitioner/example-practitioner-1' },
    authoredOn: '2026-06-01',
    dosageInstruction: [{ text: 'Take 1 tablet by mouth three times daily', route: { coding: [{ system: 'http://snomed.info/sct', code: '26643006', display: 'Oral' }] } }],
  },
};

/** A representative sample payload for the "Use sample FHIR {resource}" shortcut — falls back to a
 *  minimal generic shape for any resource type outside the curated set above. */
export function samplePayloadJsonFor(resource: string): string {
  const sample = SAMPLE_PAYLOADS[resource] ?? {
    resourceType: resource,
    id: `example-${resource.toLowerCase()}-1`,
    status: 'active',
    subject: { reference: 'Patient/example-patient-1' },
  };
  return JSON.stringify(sample, null, 2);
}
