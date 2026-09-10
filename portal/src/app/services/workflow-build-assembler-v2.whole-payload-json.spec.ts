import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';

/**
 * Regression cover for mapping the WHOLE fetched payload as JSON into one destination column (a
 * "wholeNodeAsJson" row whose source node is the resource's own root). field-mapping-model's
 * serializeRowsFlat writes the group's node id as the row path, and for the root that id is just the
 * resourceType — so this previously became "$.Patient", which JsonMappingEngine.ResolveAll resolves to
 * nothing, writing NULL into the target column on every record. Only "$" means the whole document.
 */
describe('WorkflowBuildAssemblerServiceV2 — whole-payload JSON path', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  /** toJsonPath is private; this spec reaches it directly rather than assembling a whole canvas graph. */
  const toJsonPath = (path: string, resourceType: string): string =>
    (
      service as unknown as {
        toJsonPath: (p: string, r: string) => string;
      }
    ).toJsonPath(path, resourceType);

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

  it('maps the resource root node to the whole-document path', () => {
    expect(toJsonPath('Patient', 'Patient')).toBe('$');
    expect(toJsonPath('  Observation  ', 'Observation')).toBe('$');
  });

  it('still maps a nested group/leaf path relative to the resource', () => {
    expect(toJsonPath('Patient.address', 'Patient')).toBe('$.address');
    expect(toJsonPath('Patient.name.family', 'Patient')).toBe('$.name.family');
  });

  it('leaves an already-JsonPath-shaped path untouched', () => {
    expect(toJsonPath('$.name[*].given[*]', 'Patient')).toBe('$.name[*].given[*]');
  });
});
