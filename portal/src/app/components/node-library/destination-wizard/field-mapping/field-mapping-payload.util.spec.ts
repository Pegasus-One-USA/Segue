import { parseSourcePayloadJson, samplePayloadJsonFor } from './field-mapping-payload.util';
import { buildResourceTree } from './field-mapping-tree.util';

describe('parseSourcePayloadJson', () => {
  it('rejects unparseable JSON', () => {
    const result = parseSourcePayloadJson('Patient', '{ not json');
    expect(result.ok).toBeFalse();
    if (!result.ok) expect(result.error).toContain('not valid JSON');
  });

  it('rejects empty input', () => {
    const result = parseSourcePayloadJson('Patient', '   ');
    expect(result.ok).toBeFalse();
  });

  it('rejects a JSON array at the top level', () => {
    const result = parseSourcePayloadJson('Patient', '[1,2,3]');
    expect(result.ok).toBeFalse();
  });

  it('accepts a single object whose own resourceType differs from the canvas resource — this canvas also maps non-FHIR sources', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({ resourceType: 'Observation', id: '1' }));
    expect(result.ok).toBeTrue();
    if (!result.ok) return;
    expect(result.fields.map(f => f.path)).toEqual(['Patient.id']);
    expect(result.declaredResourceType).toBe('Observation');
  });

  it('rejects a Bundle with no entries matching the canvas resource, naming what it found instead', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({
      resourceType: 'Bundle',
      entry: [{ resource: { resourceType: 'Observation', id: '1' } }],
    }));
    expect(result.ok).toBeFalse();
    if (!result.ok) expect(result.error).toContain('Observation');
  });

  it('flattens a direct resource into dot-path fields with inferred value types', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({
      resourceType: 'Patient',
      id: 'abc',
      active: true,
      birthDate: '1990-01-01',
    }));
    expect(result.ok).toBeTrue();
    if (!result.ok) return;

    const byPath = Object.fromEntries(result.fields.map(f => [f.path, f]));
    expect(byPath['Patient.id'].valueType).toBe('String');
    expect(byPath['Patient.active'].valueType).toBe('Boolean');
    expect(byPath['Patient.birthDate'].valueType).toBe('Date');
    expect(byPath['Patient.id'].arrays).toBeUndefined();
  });

  it('marks fields nested under an array-of-objects with the ancestor group path, not the leaf itself', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({
      resourceType: 'Patient',
      name: [{ family: 'Doe', given: ['Jane', 'Marie'] }],
    }));
    expect(result.ok).toBeTrue();
    if (!result.ok) return;

    const family = result.fields.find(f => f.path === 'Patient.name.family')!;
    const given = result.fields.find(f => f.path === 'Patient.name.given')!;
    expect(family.arrays).toEqual(['name']);
    // "given" is itself a repeating primitive inside each name entry, but per the real backend
    // catalog's convention only ancestor *group* paths are recorded — not the leaf's own path.
    expect(given.arrays).toEqual(['name']);
  });

  it('recurses through nested arrays of objects (extensions within extensions)', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({
      resourceType: 'Patient',
      extension: [{
        url: 'us-core-race',
        extension: [{ url: 'ombCategory', valueCoding: { code: '2106-3' } }],
      }],
    }));
    expect(result.ok).toBeTrue();
    if (!result.ok) return;

    const code = result.fields.find(f => f.path === 'Patient.extension.extension.valueCoding.code');
    expect(code).toBeDefined();
    expect(code!.arrays).toEqual(['extension', 'extension.extension']);

    // The resulting fields build into a properly nested tree, not a flat list.
    const tree = buildResourceTree('Patient', result.fields);
    const extGroup = tree.children.find(c => c.label === 'Extension')!;
    expect(extGroup.kind).toBe('group');
    expect(extGroup.isArray).toBeTrue();
  });

  it('extracts and merges matching resources out of a Bundle', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({
      resourceType: 'Bundle',
      entry: [
        { resource: { resourceType: 'Patient', id: '1', gender: 'female' } },
        { resource: { resourceType: 'Encounter', id: '2' } },
        { resource: { resourceType: 'Patient', id: '3', birthDate: '2000-01-01' } },
      ],
    }));
    expect(result.ok).toBeTrue();
    if (!result.ok) return;

    const paths = result.fields.map(f => f.path).sort();
    expect(paths).toEqual(['Patient.birthDate', 'Patient.gender', 'Patient.id']);
  });

  it('skips empty arrays and arrays-of-arrays without producing fields', () => {
    const result = parseSourcePayloadJson('Patient', JSON.stringify({
      resourceType: 'Patient',
      id: '1',
      contact: [],
      weird: [[1, 2], [3, 4]],
    }));
    expect(result.ok).toBeTrue();
    if (!result.ok) return;
    expect(result.fields.map(f => f.jsonPath)).toEqual(['id']);
  });
});

describe('samplePayloadJsonFor', () => {
  it('returns valid, matching-resourceType JSON for a curated resource', () => {
    const json = samplePayloadJsonFor('Patient');
    const parsed = JSON.parse(json);
    expect(parsed.resourceType).toBe('Patient');
  });

  it('falls back to a minimal generic shape for an uncurated resource type', () => {
    const json = samplePayloadJsonFor('DiagnosticReport');
    const parsed = JSON.parse(json);
    expect(parsed.resourceType).toBe('DiagnosticReport');
    expect(parsed.id).toBeTruthy();
  });
});
