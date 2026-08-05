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

// ── Sample payloads for the modal's "Use sample FHIR {resource}" shortcut ──────────────────────────
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
