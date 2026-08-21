import { FhirElement, resolveParentReferenceField } from './mapping-catalog.service';

/**
 * Mirrors the backend ParentReferenceResolverTests (FHIRBridge.UnitTests) field-for-field — the two
 * implementations must stay in sync, since this one only drives the wizard's UI preview/auto-lock while
 * the backend independently re-validates at save time.
 */
describe('resolveParentReferenceField', () => {
  const field = (
    fhirPath: string,
    referenceTargetTypes: string[],
    cardinality: '0..1' | '0..*' = '0..1',
  ): FhirElement => ({
    label: fhirPath,
    jsonPath: `$.${fhirPath}`,
    fhirPath,
    cardinality,
    valueType: 'String',
    isArray: cardinality !== '0..1',
    arrays: [],
    referenceTargetTypes,
  });

  it('resolves the single candidate field when only one targets the parent', () => {
    const fields = [field('subject.reference', ['Patient', 'Group']), field('encounter.reference', ['Encounter'])];
    expect(resolveParentReferenceField(fields, 'Patient')?.fhirPath).toBe('subject.reference');
  });

  it('returns null when no field can target the parent resource type', () => {
    const fields = [field('subject.reference', ['Patient', 'Group'])];
    expect(resolveParentReferenceField(fields, 'Practitioner')).toBeNull();
  });

  it('prefers the more specific field when multiple candidates target the parent', () => {
    const fields = [
      field('performer.reference', ['Practitioner', 'PractitionerRole', 'Organization', 'Patient', 'RelatedPerson']),
      field('subject.reference', ['Patient', 'Group']),
    ];
    expect(resolveParentReferenceField(fields, 'Patient')?.fhirPath).toBe('subject.reference');
  });

  it('prefers singular cardinality when specificity is equal', () => {
    const fields = [field('performer.reference', ['Patient'], '0..*'), field('subject.reference', ['Patient'], '0..1')];
    expect(resolveParentReferenceField(fields, 'Patient')?.fhirPath).toBe('subject.reference');
  });

  it('falls back to alphabetical fhirPath as a final tiebreak', () => {
    const fields = [field('zzzTarget.reference', ['Patient']), field('aaaTarget.reference', ['Patient'])];
    expect(resolveParentReferenceField(fields, 'Patient')?.fhirPath).toBe('aaaTarget.reference');
  });

  it('lets an override win even when a valid candidate exists', () => {
    const fields = [field('subject.reference', ['Patient']), field('performer.reference', ['Patient'])];
    expect(resolveParentReferenceField(fields, 'Patient', 'performer.reference')?.fhirPath).toBe('performer.reference');
  });

  it('resolves an override pointing at a nonexistent field to null', () => {
    const fields = [field('subject.reference', ['Patient'])];
    expect(resolveParentReferenceField(fields, 'Patient', 'notAField.reference')).toBeNull();
  });
});
