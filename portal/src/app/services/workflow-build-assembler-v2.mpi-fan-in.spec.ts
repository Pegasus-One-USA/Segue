import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';
import { WorkflowGraphMapperServiceV2 } from './workflow-graph-mapper-v2.service';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { WorkflowDefinitionRequest, WorkflowNodeRequest } from './workflow-api.service';

/**
 * Several sources routed into the MPI all reach its destination — the saved graph joins each of them straight to
 * it (WorkflowGraphMapperServiceV2.bypassMpi). athenahealth and eCW take their retrieval resource types (and so
 * their OAuth scopes) from what the destination consumes, and only the FIRST source feeding a destination used to
 * be credited with them: the second went out with none and failed the save with "At least one resource type is
 * required for Search (REST) retrieval".
 */
describe('WorkflowBuildAssemblerServiceV2 — several sources into one destination (MPI fan-in)', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  const ecwBackend = (name: string): Record<string, string> => ({
    Connector: 'Healow',
    __name: name,
    'App key': 'backend-system',
    'Epic audience': 'backend-system',
    'Auth method': 'jwt',
    'Client ID': `client-${name}`,
    'FHIR base URL': 'https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD',
    'Token endpoint': 'https://staging-oauthserver.ecwcloud.com/oauth/oauth2/token',
    'JWT kid': 'kid-1',
    'Key vault reference': 'fhirbridge-kv',
    'Secret Name': 'ecw-signing-key',
    'JWKS URL': 'https://example.github.io/jwks.json',
    'Retrieval method key': 'search-rest',
    'Run mode': 'manual',
  });

  const fields: Record<string, Record<string, string>> = {
    a: ecwBackend('eCW A'),
    b: ecwBackend('eCW B'),
    mpi: { __name: 'Master Patient Index', mpiIdentifiers: 'names,mrn' },
    dest: {
      __name: 'Medplum',
      dest_resources: 'Condition,Encounter',
      // An existing, untouched connection — keeps this spec about the sources, not about building a destination.
      destinationResolved: 'true',
      destinationId: 'd1111111-1111-1111-1111-111111111111',
    },
  };

  const node = (id: string, nodeType: string, category: 0 | 10 | 20, isEnabled = true): WorkflowNodeRequest => ({
    id, nodeType, category, rank: 0, subRank: 0, displayName: id,
    configurationJson: JSON.stringify(fields[id]), positionX: 0, positionY: 0, isEnabled,
  });

  /** What the mapper hands over for Source A → MPI ← Source B, MPI → Medplum: the MPI routed around. */
  const graph: WorkflowDefinitionRequest = {
    name: 'wf', description: null, isEnabled: true, trigger: null,
    nodes: [
      node('a', 'EClinicalWorksSourceNode', 0),
      node('b', 'EClinicalWorksSourceNode', 0),
      node('mpi', 'PatientMatchingNode', 10, false),
      node('dest', 'MedplumDestinationNode', 20),
    ],
    edges: [{ fromNodeId: 'a', toNodeId: 'dest' }, { fromNodeId: 'b', toNodeId: 'dest' }],
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        WorkflowBuildAssemblerServiceV2,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: WorkflowGraphMapperServiceV2, useValue: { toRequest: () => structuredClone(graph) } },
        { provide: PipelineStoreV2, useValue: { byId: (id: string) => ({ id, fields: fields[id] }) } },
      ],
    });
    service = TestBed.inject(WorkflowBuildAssemblerServiceV2);
  });

  it('gives every source feeding the destination its resource types, not just the first', () => {
    const request = service.assemble('wf', null, null, null);
    const resourceTypesFor = (nodeId: string) =>
      request.sources?.find(spec => spec.nodeId === nodeId)?.source.retrieval?.resourceTypes;

    expect(resourceTypesFor('a')).toEqual(['Condition', 'Encounter']);
    expect(resourceTypesFor('b')).toEqual(['Condition', 'Encounter']);
  });
});
