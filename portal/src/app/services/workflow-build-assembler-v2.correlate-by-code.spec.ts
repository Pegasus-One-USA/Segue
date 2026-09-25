import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';

/**
 * The field-mapping canvas's "Match criteria" instance selection only ever hands over a bare sibling field
 * name (DestMappingRow.correlationSiblingField) — field-mapping-model.ts's own catalog jsonPath is
 * frequently unavailable at that layer (see MappingSourceRef.jsonPath's "when present" doc comment), so the
 * real absolute MappingFieldRequest.correlationCodeJsonPath is derived here instead, off the row's own
 * `arrays` metadata (always present — see buildMappingForResource).
 *
 * The derivation runs the row's own INNERMOST repeating ancestor (arrays[length - 1] — the same one
 * arrayAncestorLabel() in the join-popover shows as "<X> repeats — which instance?") back through
 * toJsonPath's own wildcarding, then appends the bare sibling field name onto THAT — never onto the mapped
 * field's own full path. This is what correctly handles a sibling reached through a further NON-REPEATING
 * segment past the array (e.g. "contact[*].address.city" correlated by a contact-level "relationship" —
 * see the two-level-plus-nesting test below): a naive "swap the mapped field's own last path segment"
 * string trick got this wrong, producing "contact[*].address.relationship" (address has no such property)
 * instead of "contact[*].relationship".
 */
describe('WorkflowBuildAssemblerServiceV2 — CorrelateByCode sibling path derivation', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  /** siblingCorrelationJsonPath is private; this spec reaches it directly, mirroring this file family's own
   *  toJsonPath spec (workflow-build-assembler-v2.array-wildcard.spec.ts). */
  const siblingCorrelationJsonPath = (
    arrays: readonly string[], resourceType: string, siblingField: string,
  ): string | undefined =>
    (
      service as unknown as {
        siblingCorrelationJsonPath: (a: readonly string[], r: string, f: string) => string | undefined;
      }
    ).siblingCorrelationJsonPath(arrays, resourceType, siblingField);

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

  it('builds the sibling path from a single-level repeating ancestor', () => {
    expect(siblingCorrelationJsonPath(['telecom'], 'Patient', 'use')).toBe('$.telecom[*].use');
  });

  it('correlates within the SAME innermost item for a two-level nested repeating structure, not the outer one', () => {
    // Regression guard for the originally reported bug: correlating $.contact[*].telecom[*].value by a
    // sibling "system" must land under the SAME telecom item ("$.contact[*].telecom[*].system"), not just
    // within the same contact — the outer-only match would return that contact's FIRST telecom value
    // regardless of whether ITS OWN system actually satisfied the criteria.
    expect(siblingCorrelationJsonPath(['contact', 'contact.telecom'], 'Patient', 'system'))
      .toBe('$.contact[*].telecom[*].system');
  });

  it('correlates at the OUTER repeating level when the mapped field goes one more NON-repeating level deeper than its sibling', () => {
    // Regression guard for a second real bug found on review: a field mapped through a nested
    // non-repeating sub-object past the array (e.g. Patient.contact[].address.city) correlated by a
    // sibling that belongs to the CONTACT entry itself (e.g. "relationship"), not to "address" (address
    // has no such property at all). Only "contact" is a genuine repeating ancestor here — "address" never
    // appears in `arrays`, since it is not itself a repeating element — so the innermost ancestor is
    // correctly just "contact", regardless of how much further non-repeating nesting the mapped field's
    // OWN path goes on to cross.
    expect(siblingCorrelationJsonPath(['contact'], 'Patient', 'relationship')).toBe('$.contact[*].relationship');
  });

  it('is undefined when there is no repeating ancestor at all', () => {
    expect(siblingCorrelationJsonPath([], 'Patient', 'use')).toBeUndefined();
  });

  it('KNOWN LIMITATION: a sibling of a scalar leaf array\'s own enclosing object is not resolvable — there is no data to tell a scalar array ("given", plain strings) apart from an object array ("telecom", each item its own sibling fields) from `arrays` alone', () => {
    // "given[*]" is a plain array of strings with no sibling properties of its own at all — "family"
    // actually belongs to the ENCLOSING name[*] object, not to a specific given item, so the semantically
    // correct path would skip back out to "$.name[*].family". Nothing in `arrays` distinguishes this shape
    // from a genuine two-level nested OBJECT array (contact.telecom above), so this case is not resolved
    // correctly today — documented here rather than silently discovered later. "Match criteria" against a
    // field whose own innermost array ancestor is a scalar array is not a well-formed configuration in the
    // first place (there is no real sibling to match against), so this has not been reported as a live bug.
    expect(siblingCorrelationJsonPath(['name', 'name.given'], 'Patient', 'family'))
      .toBe('$.name[*].given[*].family');
  });

  it('works directly off row.arrays, with no catalog jsonPath needed at all', () => {
    // The exact scenario reported live: a source with NO catalog jsonPath, only `arrays` — this no longer
    // even needs a jsonPath (naive-fallback or catalog-supplied) to derive the sibling path from.
    expect(siblingCorrelationJsonPath(['telecom'], 'Patient', 'use')).toBe('$.telecom[*].use');
  });
});
