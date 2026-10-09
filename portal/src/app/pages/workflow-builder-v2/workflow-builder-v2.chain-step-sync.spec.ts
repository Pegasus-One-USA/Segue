import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { WorkflowBuilderV2Component } from './workflow-builder-v2.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { TransformationRulesService } from '../../components/node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { DeIdentificationProfileService } from '../../destination-connections/services/deidentification-profile.service';
import { WorkflowApiService } from '../../services/workflow-api.service';
import { WorkflowGraphMapperServiceV2 } from '../../services/workflow-graph-mapper-v2.service';
import { WorkflowBuildAssemblerServiceV2 } from '../../services/workflow-build-assembler-v2.service';
import { PermissionService } from '../../auth/services/permission.service';
import { ToastService } from '../../services/toast.service';
import { CanvasNode } from '../../models/node-v2.model';

/**
 * The builder adds a Transformation / De-identification step to a destination's chain when that workflow has
 * rules for it (syncChainNodes runs after every "Map fields" save). It now also takes the step away again once
 * the server confirms nothing is left for it to apply — previously a step stayed on the canvas, empty, after
 * its last rule was removed. A failed lookup changes nothing, in either direction.
 */
describe('WorkflowBuilderV2 — chain steps follow their rules', () => {
  const WORKFLOW_ID = 'wf-1';
  let store: PipelineStoreV2;
  let builder: {
    syncChainNodes(destination: CanvasNode): void;
    onTransformSelected(e: object): void;
    currentWorkflowId: { set(id: string): void };
  };
  let transformationRules: Observable<object[]>;
  let profiles: Observable<object[]>;
  let deIdRules: Observable<object[]>;

  const transformationRule = {
    resourcePipelineRouteId: WORKFLOW_ID, resourceType: 'Patient', destinationField: 'PatientName',
    sourceField: null, deIdentificationProfileId: null, isEnabled: true,
  };
  const ownedProfile = { id: 'profile-1', name: WORKFLOW_ID };
  const deIdRule = { deIdentificationProfileId: 'profile-1', isEnabled: true };

  const step = (id: string, transformId: string, x: number, extra: Record<string, string> = {}): CanvasNode =>
    ({ id, kind: 'transform', transformId, x, y: 0, fields: { __name: transformId, ...extra } }) as unknown as CanvasNode;

  /** Source → Mapping → Transformation → De-identification → SQL Server, as on the ECW Backend workflow. By default
   *  both steps were added BY THE BUILDER (__autoAdded) and the destination redacts with this workflow's policy. */
  function seedChain(options: { autoAdded?: boolean; policy?: string } = {}): CanvasNode {
    const marker: Record<string, string> = options.autoAdded === false ? {} : { __autoAdded: 'true' };
    const destination = {
      ...step('dest', 'dest-sqlserver', 1200),
      fields: {
        __name: 'SQL Server',
        dest_mappings: JSON.stringify([{ resource: 'Patient', column: 'PatientName' }]),
        deIdentificationProfileId: options.policy ?? 'profile-1',
      },
    } as unknown as CanvasNode;
    const nodes: CanvasNode[] = [
      { id: 'src', x: 0, y: 0, fields: { __name: 'ECW' } } as unknown as CanvasNode,
      step('map', 'field-mapping', 300), step('tra', 'transformation', 600, marker),
      step('deid', 'deidentification', 900, marker),
      destination,
    ];
    const ids = nodes.map(n => n.id);
    store.loadGraph(nodes, ids.slice(1).map((to, i) => ({ id: `e${i}`, from: ids[i], to })));
    return destination;
  }

  const chain = () => {
    const ids: string[] = [];
    let cursor: string | undefined = 'src';
    while (cursor) {
      ids.push(cursor);
      cursor = store.outboundEdges(cursor)[0]?.to;
    }
    return ids;
  };

  beforeEach(() => {
    transformationRules = of([transformationRule, deIdRule]);
    profiles = of([ownedProfile]);
    deIdRules = of([transformationRule, deIdRule]);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: TransformationRulesService, useValue: {
            // The transformation lookup filters by destination type; the de-identification one asks for everything.
            list: (filter: { destinationType?: string }) => (filter.destinationType ? transformationRules : deIdRules) } },
        { provide: DeIdentificationProfileService, useValue: { list: () => profiles } },
        { provide: WorkflowApiService, useValue: { catalog: () => [], loadCatalog: () => of([]) } },
        { provide: WorkflowGraphMapperServiceV2, useValue: {} },
        { provide: WorkflowBuildAssemblerServiceV2, useValue: {} },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show', 'success', 'error', 'warning']) },
      ],
    });
    // The constructor alone is enough: syncChainNodes is what is under test, not the template or ngOnInit.
    TestBed.overrideComponent(WorkflowBuilderV2Component, { set: { template: '', imports: [] } });
    store = TestBed.inject(PipelineStoreV2);
    builder = TestBed.createComponent(WorkflowBuilderV2Component).componentInstance as unknown as typeof builder;
    builder.currentWorkflowId.set(WORKFLOW_ID);
  });

  it('keeps both steps while their rules exist', () => {
    const destination = seedChain();

    builder.syncChainNodes(destination);

    expect(chain()).toEqual(['src', 'map', 'tra', 'deid', 'dest']);
  });

  it('removes both steps once every rule is gone, and reconnects the chain', () => {
    const destination = seedChain();
    transformationRules = of([]);
    deIdRules = of([]);

    builder.syncChainNodes(destination);

    expect(chain()).toEqual(['src', 'map', 'dest']);
    expect(store.byId('tra')).toBeUndefined();
    expect(store.byId('deid')).toBeUndefined();
  });

  it('removes only the step whose rules are gone', () => {
    const destination = seedChain();
    transformationRules = of([]);

    builder.syncChainNodes(destination);

    expect(chain()).toEqual(['src', 'map', 'deid', 'dest']);
  });

  it('removes De-identification when the workflow\'s own policy, which the step uses, has only disabled rules', () => {
    const destination = seedChain();
    deIdRules = of([transformationRule, { ...deIdRule, isEnabled: false }]);

    builder.syncChainNodes(destination);

    expect(store.byId('deid')).toBeUndefined();
  });

  it('never removes De-identification when the step uses a shared or differently named policy', () => {
    // The destination's policy picker points at a shared policy: this workflow's own (empty) policy is irrelevant.
    const destination = seedChain({ policy: 'shared-safe-harbor' });
    deIdRules = of([]);
    builder.syncChainNodes(destination);
    expect(store.byId('deid')).toBeDefined();

    // No policy is named after the workflow at all: the step may still use any other policy, or the default.
    const reseeded = seedChain();
    profiles = of([{ id: 'other', name: 'another-workflow' }]);
    builder.syncChainNodes(reseeded);
    expect(store.byId('deid')).toBeDefined();
  });

  it('never removes a step the user placed (no __autoAdded marker), even with no rules', () => {
    const destination = seedChain({ autoAdded: false });
    transformationRules = of([]);
    deIdRules = of([]);

    builder.syncChainNodes(destination);

    expect(chain()).toEqual(['src', 'map', 'tra', 'deid', 'dest']);
  });

  it('marks the steps it adds, so only those can be taken away later', () => {
    const destination = seedChain();
    transformationRules = of([]);
    deIdRules = of([]);
    builder.syncChainNodes(destination);

    transformationRules = of([transformationRule]);
    deIdRules = of([deIdRule]);
    builder.syncChainNodes(destination);

    const added = chain().map(id => store.byId(id)!).filter(n => ['transformation', 'deidentification']
      .includes((n as unknown as { transformId: string }).transformId));
    expect(added.length).toBe(2);
    expect(added.every(n => n.fields['__autoAdded'] === 'true')).toBeTrue();
  });

  it('ignores a stale "no rules" answer that arrives after a newer one added the step back', () => {
    const destination = seedChain();
    const olderLookup = new Subject<object[]>();
    transformationRules = olderLookup;           // first save: its lookup is still in flight…
    builder.syncChainNodes(destination);
    transformationRules = of([transformationRule]);   // …second save answers first: rules exist
    builder.syncChainNodes(destination);

    olderLookup.next([]);                         // the older, now stale, "no rules" lands last

    expect(store.byId('tra')).toBeDefined();
  });

  it('keeps every link through a removed step, not only the first', () => {
    const destination = seedChain();
    store.addNode({ id: 'audit', x: 600, y: 300, fields: { __name: 'Audit' } } as unknown as CanvasNode);
    store.addEdge({ id: 'e-extra', from: 'tra', to: 'audit' });
    transformationRules = of([]);

    builder.syncChainNodes(destination);

    expect(store.outboundEdges('map').map(e => e.to).sort()).toEqual(['audit', 'deid']);
  });

  it('a failed lookup removes nothing', () => {
    const destination = seedChain();
    transformationRules = throwError(() => new Error('rules API down'));
    profiles = throwError(() => new Error('profiles API down'));

    builder.syncChainNodes(destination);

    expect(chain()).toEqual(['src', 'map', 'tra', 'deid', 'dest']);
  });

  it('still adds the steps back when rules are authored again', () => {
    const destination = seedChain();
    transformationRules = of([]);
    deIdRules = of([]);
    builder.syncChainNodes(destination);

    transformationRules = of([transformationRule]);
    deIdRules = of([deIdRule]);
    builder.syncChainNodes(destination);

    expect(chain().length).toBe(5);
    expect(chain().slice(-1)).toEqual(['dest']);
  });

  it('lays the remaining chain out left-to-right with no gap', () => {
    const destination = seedChain();
    transformationRules = of([]);
    deIdRules = of([]);

    builder.syncChainNodes(destination);

    expect(chain().map(id => store.byId(id)!.x)).toEqual([0, 300, 600]);
  });
  it('never auto-removes a step once the user has opened and saved it (marker claimed)', () => {
    const destination = seedChain();
    // The node library's edit flow for a chain step: the destination is what is saved, chainNodeId names the step.
    builder.onTransformSelected({
      attachNode: destination, transformId: 'dest-sqlserver', status: 'enabled', config: {},
      editNodeId: 'dest', chainLabel: 'Transformation', chainNodeId: 'tra',
    });
    expect(store.byId('tra')!.fields['__autoAdded']).toBeUndefined();

    transformationRules = of([]);
    deIdRules = of([]);
    builder.syncChainNodes(store.byId('dest')!);

    expect(store.byId('tra')).toBeDefined();         // claimed by the user: kept
    expect(store.byId('deid')).toBeUndefined();      // still only builder-added: removed
  });

  it('removes nothing while no column is mapped yet — only a confirmed server answer removes a step', () => {
    const destination = seedChain();
    store.updateNode('dest', { fields: { ...destination.fields, dest_mappings: '[]' } });
    transformationRules = of([]);

    builder.syncChainNodes(store.byId('dest')!);

    expect(store.byId('tra')).toBeDefined();
  });
});
