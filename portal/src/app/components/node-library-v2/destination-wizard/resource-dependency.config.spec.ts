import {
  requiredClosureFor, recommendedFor, lockingDependentsOf, dependencyRankFor, sortByDependencyRank,
} from './resource-dependency.config';

describe('requiredClosureFor', () => {
  it('returns an empty list for a resource with no dependencies', () => {
    expect(requiredClosureFor('Patient')).toEqual([]);
    expect(requiredClosureFor('Practitioner')).toEqual([]);
  });

  it('returns the direct requirement for a single-level dependency', () => {
    expect(requiredClosureFor('Encounter')).toEqual(['Patient']);
  });

  it('resolves multi-level chains transitively (MedicationAdministration -> MedicationRequest -> Patient)', () => {
    const closure = requiredClosureFor('MedicationAdministration');
    expect(closure).toContain('MedicationRequest');
    expect(closure).toContain('Patient');
    expect(closure.length).toBe(2);
  });

  it('dedupes when two required branches both lead back to the same resource (DiagnosticReport)', () => {
    // DiagnosticReport requires [Patient, Observation], and Observation itself requires [Patient].
    const closure = requiredClosureFor('DiagnosticReport');
    expect(closure).toContain('Patient');
    expect(closure).toContain('Observation');
    expect(closure.length).toBe(2);
  });

  it('never includes the resource itself', () => {
    expect(requiredClosureFor('Encounter')).not.toContain('Encounter');
  });

  it('defaults to no dependencies for an unknown resource — data-driven, not a hardcoded switch', () => {
    expect(requiredClosureFor('SomeFutureResource')).toEqual([]);
  });
});

describe('recommendedFor', () => {
  it('returns the authored recommendations as-is (not transitively expanded)', () => {
    expect(recommendedFor('Observation')).toEqual(['Encounter', 'Practitioner', 'ServiceRequest', 'Specimen']);
  });

  it('returns an empty list for a resource with no recommendations', () => {
    expect(recommendedFor('AllergyIntolerance')).toEqual([]);
  });

  it('recommends Location alongside Encounter (Encounter.location) — confirmed via live Epic + Aidbox testing', () => {
    expect(recommendedFor('Encounter')).toContain('Location');
  });

  it('recommends ServiceRequest and Specimen alongside Observation (Observation.basedOn / .specimen)', () => {
    expect(recommendedFor('Observation')).toContain('ServiceRequest');
    expect(recommendedFor('Observation')).toContain('Specimen');
  });

  it('recommends Organization alongside Location (Location.managingOrganization)', () => {
    expect(recommendedFor('Location')).toEqual(['Organization']);
  });

  it('recommends Practitioner alongside Specimen (Specimen.collection.collector)', () => {
    expect(recommendedFor('Specimen')).toEqual(['Practitioner']);
  });
});

describe('lockingDependentsOf', () => {
  it('is empty when nothing selected depends on the resource', () => {
    expect(lockingDependentsOf('Patient', ['Patient'])).toEqual([]);
  });

  it('names the selected resource(s) that require it', () => {
    expect(lockingDependentsOf('Patient', ['Patient', 'Observation'])).toEqual(['Observation']);
  });

  it('unlocks once the dependent is removed from the selection', () => {
    expect(lockingDependentsOf('Patient', ['Patient'])).toEqual([]);
  });

  it('follows transitive chains (Patient stays locked via MedicationAdministration even though MedicationRequest, not Patient, is its direct requirement)', () => {
    expect(lockingDependentsOf('Patient', ['Patient', 'MedicationRequest', 'MedicationAdministration']))
      .toEqual(['MedicationRequest', 'MedicationAdministration']);
  });

  it('never reports the resource as its own dependent', () => {
    expect(lockingDependentsOf('Observation', ['Observation'])).toEqual([]);
  });
});

describe('dependencyRankFor', () => {
  it('ranks anything with no required dependency at 0', () => {
    expect(dependencyRankFor('Patient')).toBe(0);
    expect(dependencyRankFor('Practitioner')).toBe(0);
  });

  it('ranks a single-level dependency at 1', () => {
    expect(dependencyRankFor('Encounter')).toBe(1);
    expect(dependencyRankFor('AllergyIntolerance')).toBe(1);
  });

  it('ranks a resource above the highest rank among its required dependencies, not just its direct count', () => {
    // DiagnosticReport requires [Patient (rank 0), Observation (rank 1)] -> rank 2, not 1.
    expect(dependencyRankFor('DiagnosticReport')).toBe(2);
    // MedicationAdministration requires [Patient (rank 0), MedicationRequest (rank 1)] -> rank 2.
    expect(dependencyRankFor('MedicationAdministration')).toBe(2);
  });
});

describe('sortByDependencyRank', () => {
  it('moves a prerequisite resource ahead of whatever requires it, regardless of input order', () => {
    expect(sortByDependencyRank(['Encounter', 'Patient', 'Condition', 'Practitioner', 'AllergyIntolerance']))
      .toEqual(['Patient', 'Practitioner', 'Encounter', 'Condition', 'AllergyIntolerance']);
  });

  it('places a rank-2 resource after both its rank-0 and rank-1 requirements', () => {
    const sorted = sortByDependencyRank(['DiagnosticReport', 'Observation', 'Patient']);
    expect(sorted.indexOf('Patient')).toBeLessThan(sorted.indexOf('Observation'));
    expect(sorted.indexOf('Observation')).toBeLessThan(sorted.indexOf('DiagnosticReport'));
  });

  it('is stable — same-rank resources keep their original relative order', () => {
    expect(sortByDependencyRank(['Practitioner', 'Patient'])).toEqual(['Practitioner', 'Patient']);
  });

  it('does not mutate the input array', () => {
    const input = ['Encounter', 'Patient'];
    sortByDependencyRank(input);
    expect(input).toEqual(['Encounter', 'Patient']);
  });
});
