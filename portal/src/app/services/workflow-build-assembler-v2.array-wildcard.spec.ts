import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';

/**
 * Regression cover for an array-nested source path losing its "[*]" across a save→reload→save cycle.
 *
 * A row reloaded from a saved Mapping document keeps its array-ancestor chain (applyMappingSummaryDocument
 * reconstructs `arrays` from the column's arrayContext) but NOT its jsonPath — that field is not part of the
 * summary schema — so the next save falls through to toJsonPath. Before this fix that fallback ignored
 * `arrays` entirely: "Patient.name.text" became "$.name.text", which matches nothing against an array-valued
 * `name`. The Mapping node then skipped the field with no value, no lineage row and no error, the run still
 * reported Succeeded, and the destination column silently held NULL — re-degrading on every subsequent save,
 * which is why such a field could work once and then break the moment any unrelated field was added.
 */
describe('WorkflowBuildAssemblerServiceV2 — array wildcards in the fallback JSONPath', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  /** toJsonPath is private; this spec reaches it directly rather than assembling a whole canvas graph. */
  const toJsonPath = (path: string, resourceType: string, arrays?: readonly string[]): string =>
    (
      service as unknown as {
        toJsonPath: (p: string, r: string, a?: readonly string[]) => string;
      }
    ).toJsonPath(path, resourceType, arrays);

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        WorkflowBuildAssemblerServiceV2,
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(WorkflowBuildAssemblerServiceV2);
  });

  it('wildcards a single array ancestor', () => {
    expect(toJsonPath('Patient.name.text', 'Patient', ['name'])).toBe('$.name[*].text');
    expect(toJsonPath('Patient.address.city', 'Patient', ['address'])).toBe('$.address[*].city');
  });

  it('wildcards a nested array ancestor chain longest-first', () => {
    expect(toJsonPath('Observation.code.coding.code', 'Observation', ['code.coding'])).toBe(
      '$.code.coding[*].code',
    );
    expect(
      toJsonPath('Patient.meta.security.system', 'Patient', ['meta.security']),
    ).toBe('$.meta.security[*].system');
  });

  it('wildcards an ancestor that is the whole path', () => {
    expect(toJsonPath('Patient.name', 'Patient', ['name'])).toBe('$.name[*]');
  });

  it('accepts an ancestor already carrying its wildcard, and one qualified by resource type', () => {
    expect(toJsonPath('Patient.name.text', 'Patient', ['name[*]'])).toBe('$.name[*].text');
    expect(toJsonPath('Patient.name.text', 'Patient', ['Patient.name'])).toBe('$.name[*].text');
  });

  it('leaves a path alone when it has no array ancestors', () => {
    expect(toJsonPath('Patient.birthDate', 'Patient', [])).toBe('$.birthDate');
    expect(toJsonPath('Patient.gender', 'Patient')).toBe('$.gender');
  });

  it('leaves an ancestor that is not a prefix of this path alone', () => {
    expect(toJsonPath('Patient.birthDate', 'Patient', ['name'])).toBe('$.birthDate');
  });

  it('still maps the resource root node to the whole-document path', () => {
    expect(toJsonPath('Patient', 'Patient', ['name'])).toBe('$');
  });

  it('does not degrade across repeated save→reload cycles', () => {
    // Each cycle re-derives the path from the same (path, arrays) pair a reloaded row carries. The result
    // must be stable: it was the SECOND save that used to strip the wildcard and silently null the column.
    let path = toJsonPath('Patient.name.text', 'Patient', ['name']);
    for (let i = 0; i < 3; i++) {
      expect(path).toBe('$.name[*].text');
      // A row whose jsonPath survived is passed straight through by the "$" short-circuit.
      path = toJsonPath(path, 'Patient', ['name']);
    }
  });
});
