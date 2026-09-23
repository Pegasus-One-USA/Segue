import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';

/**
 * The field-mapping canvas's "Match criteria" instance selection (op "=") only ever hands over a bare
 * sibling field name (DestMappingRow.correlationSiblingField) — field-mapping-model.ts's own catalog jsonPath
 * is frequently unavailable at that layer (see MappingSourceRef.jsonPath's "when present" doc comment), so
 * the real absolute MappingFieldRequest.correlationCodeJsonPath is derived here instead, off THIS row's own
 * always-resolved `jsonPath` (falls back to the naive toJsonPath conversion when the catalog didn't supply
 * one) — see buildMappingForResource. This regression-guards the exact bug reported live: a field whose
 * source had no catalog jsonPath silently kept the "Preview only" approximation instead of ever reaching
 * CorrelateByCode, because the OLD implementation tried (and failed) to derive the sibling path from that
 * same unavailable catalog jsonPath one layer up, in field-mapping-model.ts.
 */
describe('WorkflowBuildAssemblerServiceV2 — CorrelateByCode sibling path derivation', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  /** siblingCorrelationJsonPath is private; this spec reaches it directly, mirroring this file family's own
   *  toJsonPath spec (workflow-build-assembler-v2.array-wildcard.spec.ts). */
  const siblingCorrelationJsonPath = (jsonPath: string, siblingField: string): string | undefined =>
    (
      service as unknown as {
        siblingCorrelationJsonPath: (j: string, f: string) => string | undefined;
      }
    ).siblingCorrelationJsonPath(jsonPath, siblingField);

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

  it('builds the sibling path by swapping the mapped field\'s own trailing leaf segment', () => {
    expect(siblingCorrelationJsonPath('$.telecom[*].value', 'use')).toBe('$.telecom[*].use');
  });

  it('correlates within the SAME repeating instance even when the mapped field\'s own path goes one level deeper', () => {
    // "given[*]" is a plain array of strings, not an object with its own siblings — "family" belongs to
    // the ENCLOSING name[*] object, not to a specific given item.
    expect(siblingCorrelationJsonPath('$.name[*].given[*]', 'family')).toBe('$.name[*].family');
  });

  it('correlates within the SAME innermost item for a two-level nested repeating structure, not the outer one', () => {
    // Regression guard for a real reported bug: the OLD implementation replaced everything after the
    // FIRST "[*]", producing "$.contact[*].system" — a path that doesn't exist at all (system lives under
    // contact[*].telecom[*], not directly under contact[*]), so the criteria silently matched nothing.
    expect(siblingCorrelationJsonPath('$.contact[*].telecom[*].value', 'system'))
      .toBe('$.contact[*].telecom[*].system');
  });

  it('is undefined when the jsonPath has no repeating ancestor at all', () => {
    expect(siblingCorrelationJsonPath('$.gender', 'use')).toBeUndefined();
  });

  it('works off a jsonPath the naive toJsonPath fallback produced, not only a catalog-supplied one', () => {
    // The exact scenario reported live: a source with NO catalog jsonPath (only `arrays`), where this
    // service's own toJsonPath fallback is what produces "$.telecom[*].value" in the first place.
    const toJsonPath = (
      service as unknown as { toJsonPath: (p: string, r: string, a?: readonly string[]) => string }
    ).toJsonPath('Patient.telecom.value', 'Patient', ['telecom']);

    expect(toJsonPath).toBe('$.telecom[*].value');
    expect(siblingCorrelationJsonPath(toJsonPath, 'use')).toBe('$.telecom[*].use');
  });
});
