import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
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
  let builder: { syncChainNodes(destination: CanvasNode): void; currentWorkflowId: { set(id: string): void } };
  let transformationRules: Observable<object[]>;
  let profiles: Observable<object[]>;
  let deIdRules: Observable<object[]>;

  const transformationRule = {
    resourcePipelineRouteId: WORKFLOW_ID, resourceType: 'Patient', destinationField: 'PatientName',
    sourceField: null, deIdentificationProfileId: null, isEnabled: true,
  };
  const ownedProfile = { id: 'profile-1', name: WORKFLOW_ID };
  const deIdRule = { deIdentificationProfileId: 'profile-1', isEnabled: true };

  const step = (id: string, transformId: string, x: number): CanvasNode =>
    ({ id, kind: 'transform', transformId, x, y: 0, fields: { __name: transformId } }) as unknown as CanvasNode;

  /** Source → Mapping → Transformation → De-identification → SQL Server, as on the ECW Backend workflow. */
  function seedChain(): CanvasNode {
    const destination = {
      ...step('dest', 'dest-sqlserver', 1200),
      fields: { __name: 'SQL Server', dest_mappings: JSON.stringify([{ resource: 'Patient', column: 'PatientName' }]) },
    } as unknown as CanvasNode;
    const nodes: CanvasNode[] = [
      { id: 'src', x: 0, y: 0, fields: { __name: 'ECW' } } as unknown as CanvasNode,
      step('map', 'field-mapping', 300), step('tra', 'transformation', 600), step('deid', 'deidentification', 900),
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

  it('removes De-identification when its policy has only disabled rules, or no policy belongs to the workflow', () => {
    const destination = seedChain();
    deIdRules = of([transformationRule, { ...deIdRule, isEnabled: false }]);
    builder.syncChainNodes(destination);
    expect(store.byId('deid')).toBeUndefined();

    const reseeded = seedChain();
    profiles = of([{ id: 'other', name: 'another-workflow' }]);
    builder.syncChainNodes(reseeded);
    expect(store.byId('deid')).toBeUndefined();
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
});
