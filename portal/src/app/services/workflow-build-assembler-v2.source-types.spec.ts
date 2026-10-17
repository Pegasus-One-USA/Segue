import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';
import { CreateSourceConnectionRequest, WorkflowBuildRequest, WorkflowNodeRequest } from './workflow-api.service';
import { SUPPORTED_RESOURCE_TYPES } from '../data/scope-constants-v2.data';
import { SOURCE_TYPES_DECLARED_KEY } from './upstream-source-v2.util';

/**
 * The source declares what it reads: a connection's retrieval resource types (and athenahealth / eClinicalWorks
 * scopes) come from the source node's own 'Resources'. Only a legacy source node (no declared list, or a list the
 * form filled in silently before sources declared their types) still takes them from what its destinations write,
 * so a workflow saved before the source declared them builds unchanged.
 */
describe('WorkflowBuildAssemblerServiceV2 — retrieval types come from the source', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  const node = (id: string, nodeType: string, category: string, fields: Record<string, string>): WorkflowNodeRequest =>
    ({ id, nodeType, category, configurationJson: JSON.stringify(fields) }) as unknown as WorkflowNodeRequest;

  const typesBySource = (graph: WorkflowBuildRequest): Map<string, string[]> =>
    (
      service as unknown as {
        retrievalResourceTypesBySourceNodeId: (
          g: WorkflowBuildRequest,
          byId: Map<string, WorkflowNodeRequest>,
          sourceIds: Set<string>,
        ) => Map<string, string[]>;
      }
    ).retrievalResourceTypesBySourceNodeId(
      graph,
      new Map(graph.nodes.map((n) => [n.id, n])),
      new Set(graph.nodes.filter((n) => n.category === 'Source').map((n) => n.id)),
    );

  const buildSource = (fields: Record<string, string>, types: string[]): CreateSourceConnectionRequest =>
    (
      service as unknown as { buildSource: (f: Record<string, string>, t: string[]) => CreateSourceConnectionRequest }
    ).buildSource(fields, types);

  const graph = (sourceFields: Record<string, string>, destFields?: Record<string, string>): WorkflowBuildRequest =>
    ({
      nodes: [
        node('src', 'AthenahealthSourceNode', 'Source', { Connector: 'Athenahealth', ...sourceFields }),
        node('map', 'FieldMappingNode', 'Transform', {}),
        node(
          'dst',
          'SqlServerDestinationNode',
          'Destination',
          destFields ?? { dest_resources: 'Patient', dest_mappings: JSON.stringify([{ resource: 'Condition' }]) },
        ),
      ],
      edges: [
        { fromNodeId: 'src', toNodeId: 'map' },
        { fromNodeId: 'map', toNodeId: 'dst' },
      ],
    }) as unknown as WorkflowBuildRequest;

  const declared = { [SOURCE_TYPES_DECLARED_KEY]: 'true' };
  const athenaBackend = {
    Connector: 'Athenahealth',
    'App key': 'backend-system',
    'Epic audience': 'backend-system',
    'Auth method': 'secret',
    'Retrieval method key': 'search-rest',
  };
  const ecwBackend = {
    Connector: 'Healow',
    'App key': 'backend-system',
    'Epic audience': 'backend-system',
    'Auth method': 'jwt',
    'Retrieval method key': 'bulk-export',
  };
  const ecwGraph = (sourceFields: Record<string, string>, destResources: string): WorkflowBuildRequest =>
    ({
      nodes: [
        node('src', 'EClinicalWorksSourceNode', 'Source', sourceFields),
        node('dst', 'SqlServerDestinationNode', 'Destination', { dest_resources: destResources }),
      ],
      edges: [{ fromNodeId: 'src', toNodeId: 'dst' }],
    }) as unknown as WorkflowBuildRequest;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [WorkflowBuildAssemblerServiceV2, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WorkflowBuildAssemblerServiceV2);
  });

  it('uses the source node\'s declared Resources, not what the destinations write', () => {
    expect(typesBySource(graph({ ...declared, Resources: 'Patient, Observation' })).get('src')).toEqual([
      'Patient',
      'Observation',
    ]);
  });

  it('falls back to the destinations\' types for a legacy source with no declared list', () => {
    expect(typesBySource(graph({})).get('src')).toEqual(['Patient', 'Condition']);
  });

  it('a destination behind a merge counts for every legacy source feeding it, as the server\'s scope sync does', () => {
    const merged = {
      nodes: [
        node('s1', 'AthenahealthSourceNode', 'Source', { Connector: 'Athenahealth' }),
        node('s2', 'AthenahealthSourceNode', 'Source', {
          Connector: 'Athenahealth',
          'Retrieval resource type': 'Patient, Encounter, Claim',
        }),
        node('merge', 'MergeNode', 'Transform', {}),
        node('dst', 'SqlServerDestinationNode', 'Destination', { dest_resources: 'Patient, Condition' }),
      ],
      edges: [
        { fromNodeId: 's1', toNodeId: 'merge' },
        { fromNodeId: 's2', toNodeId: 'merge' },
        { fromNodeId: 'merge', toNodeId: 'dst' },
      ],
    } as unknown as WorkflowBuildRequest;
    const types = typesBySource(merged);
    expect(types.get('s1')).toEqual(['Patient', 'Condition']);
    // Not empty, so buildSource never falls back to s2's silent 'Retrieval resource type'.
    expect(types.get('s2')).toEqual(['Patient', 'Condition']);
  });

  it('a destination no source reaches counts only for a legacy source with no outgoing edges', () => {
    const loose = {
      nodes: [
        node('wired', 'AthenahealthSourceNode', 'Source', { Connector: 'Athenahealth' }),
        node('edgeless', 'AthenahealthSourceNode', 'Source', { Connector: 'Athenahealth' }),
        node('fed', 'SqlServerDestinationNode', 'Destination', { dest_resources: 'Patient' }),
        node('orphan', 'SqlServerDestinationNode', 'Destination', { dest_resources: 'Observation' }),
      ],
      edges: [{ fromNodeId: 'wired', toNodeId: 'fed' }],
    } as unknown as WorkflowBuildRequest;
    const types = typesBySource(loose);
    expect(types.get('wired')).toEqual(['Patient']);
    expect(types.get('edgeless')).toEqual(['Patient', 'Observation']);
  });

  it('drives athenahealth\'s retrieval resource types and scopes', () => {
    const request = buildSource(
      { ...athenaBackend, 'Retrieval resource type': 'Patient, Observation' },
      ['Patient', 'Observation'],
    );
    expect(request.retrieval?.resourceTypes).toEqual(['Patient', 'Observation']);
    expect(request.authentication.scopes).toEqual(['system/Patient.read', 'system/Observation.read']);
  });

  it('athenahealth: an older node\'s silent full list is legacy, so scopes stay what the destinations write', () => {
    // Before sources declared their types, an audience switch or a clone filled 'Resources' / 'Retrieval resource
    // type' with every supported type. Requesting system/X.read for all of them gets athena's whole token refused.
    const silent = SUPPORTED_RESOURCE_TYPES.join(', ');
    const fields = { ...athenaBackend, Resources: silent, 'Retrieval resource type': silent };
    const types = typesBySource(graph(fields, { dest_resources: 'Patient' })).get('src')!;
    expect(types).toEqual(['Patient']);

    const request = buildSource(fields, types);
    expect(request.authentication.scopes).toEqual(['system/Patient.read']);
    expect(request.retrieval?.resourceTypes).toEqual(['Patient']);
  });

  it('eClinicalWorks Backend: declared types drive retrieval and scopes, minus any eCW cannot scope', () => {
    const fields = { ...ecwBackend, ...declared, Resources: 'Patient, Coverage, Task' };
    const types = typesBySource(ecwGraph(fields, 'Patient')).get('src')!;
    expect(types).toEqual(['Patient', 'Coverage', 'Task']);

    const request = buildSource(fields, types);
    // eCW publishes no system/ scope for Task, and spells Coverage's as '.r'.
    expect(request.retrieval?.resourceTypes).toEqual(['Patient', 'Coverage']);
    expect(request.authentication.scopes).toEqual(['system/Patient.read', 'system/Coverage.r']);
  });

  it('eClinicalWorks Backend: a legacy node cloned with the full list falls back to what the destinations write', () => {
    const fields = { ...ecwBackend, Resources: SUPPORTED_RESOURCE_TYPES.join(', ') };
    const types = typesBySource(ecwGraph(fields, 'Patient, Condition')).get('src')!;
    expect(types).toEqual(['Patient', 'Condition']);

    const request = buildSource(fields, types);
    expect(request.retrieval?.resourceTypes).toEqual(['Patient', 'Condition']);
    expect(request.authentication.scopes).toEqual(['system/Patient.read', 'system/Condition.read']);
  });
});
