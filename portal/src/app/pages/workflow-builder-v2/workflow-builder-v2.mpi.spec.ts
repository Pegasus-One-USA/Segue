import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { WorkflowBuilderV2Component } from './workflow-builder-v2.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { PermissionService } from '../../auth/services/permission.service';
import { ToastService } from '../../services/toast.service';
import { CanvasNode, SourceNode, TransformNode, isMpiNode } from '../../models/node-v2.model';
import { WorkflowApiService, WorkflowBuildRequest, WorkflowCatalogItem, WorkflowNodeRequest } from '../../services/workflow-api.service';
import { WorkflowBuildAssemblerServiceV2 } from '../../services/workflow-build-assembler-v2.service';

/** Adding the MPI from a source's `+`: it lands directly after that source and takes over what the source fed;
 *  picked from another source's `+` once it exists, it connects that source into the same MPI. */
describe('WorkflowBuilderV2Component — adding the MPI', () => {
  let builder: WorkflowBuilderV2Component;
  let store: PipelineStoreV2;
  let toast: ToastService;

  const source: SourceNode = { id: 'src', x: 360, y: 300, connected: true, fields: { '__name': 'Epic' } };
  const destination: TransformNode =
    { id: 'dest', kind: 'transform', transformId: 'dest-csv', x: 660, y: 300, fields: { '__name': 'CSV' } };

  const addMpi = (attachNode: CanvasNode) =>
    builder.onTransformSelected({ attachNode, transformId: 'mpi', status: 'show' });
  const mpiNodes = () => store.nodes().filter(isMpiNode);
  const links = () => store.edges().map(edge => `${edge.from}->${edge.to}`).sort();

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WorkflowBuilderV2Component],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
      ],
    });
    // Only the class's graph logic is under test: an empty template keeps the canvas and node library (and their
    // wizard services) out of it, and with no detectChanges ngOnInit never loads the catalog over HTTP.
    TestBed.overrideComponent(WorkflowBuilderV2Component, { set: { imports: [], template: '' } });
    builder = TestBed.createComponent(WorkflowBuilderV2Component).componentInstance;
    store = TestBed.inject(PipelineStoreV2);
    toast = TestBed.inject(ToastService);
    store.reset();
  });

  it('adds it directly after a bare source and opens its identifier picker', () => {
    store.addNode(source);

    addMpi(source);

    const [mpi] = mpiNodes();
    expect(mpi).toBeDefined();
    expect(mpi.x).toBe(source.x + 300);
    expect(mpi.y).toBe(source.y);
    expect(links()).toEqual([`src->${mpi.id}`]);
    expect(builder['mpiDialogNodeId']()).toBe(mpi.id);
  });

  it('slots in between a source and its existing pipeline, pushing that pipeline right', () => {
    store.addNode(source);
    store.addNode(destination);
    store.addEdge({ id: 'e1', from: 'src', to: 'dest' });

    addMpi(source);

    const [mpi] = mpiNodes();
    expect(links()).toEqual([`${mpi.id}->dest`, `src->${mpi.id}`].sort());
    expect(store.byId('dest')!.x).toBe(destination.x + 300);
  });

  it('connects another bare source into the existing MPI instead of adding a second one', () => {
    const second: SourceNode = { ...source, id: 'src-2', y: 500 };
    store.addNode(source);
    store.addNode(second);
    addMpi(source);
    const [mpi] = mpiNodes();

    addMpi(second);

    expect(mpiNodes().length).toBe(1);
    expect(links()).toEqual([`src->${mpi.id}`, `src-2->${mpi.id}`].sort());
  });

  it('refuses to route a source that already has its own pipeline through the MPI', () => {
    const second: SourceNode = { ...source, id: 'src-2', y: 500 };
    const otherDestination: TransformNode = { ...destination, id: 'dest-2', y: 500 };
    store.addNode(source);
    store.addNode(second);
    store.addNode(otherDestination);
    store.addEdge({ id: 'e1', from: 'src-2', to: 'dest-2' });
    addMpi(source);
    spyOn(toast, 'warning');

    addMpi(second);

    expect(links()).toEqual([`src->${mpiNodes()[0].id}`, 'src-2->dest-2'].sort());
    expect(toast.warning).toHaveBeenCalled();
  });

  describe('saving', () => {
    const runNode = (id: string, category: 0 | 10 | 20, isEnabled = true): WorkflowNodeRequest => ({
      id, nodeType: `${id}Node`, category, rank: 0, subRank: 0, displayName: id,
      configurationJson: '{}', positionX: 0, positionY: 0, isEnabled,
    });

    function attemptSave(graph: Pick<WorkflowBuildRequest, 'nodes' | 'edges'>): { saved: jasmine.Spy } {
      store.addNode(source);
      builder['workflowName'].set('wf');
      spyOn(TestBed.inject(WorkflowApiService), 'catalog').and.returnValue([{} as WorkflowCatalogItem]);
      spyOn(TestBed.inject(WorkflowBuildAssemblerServiceV2), 'assemble').and.returnValue(
        { name: 'wf', description: null, isEnabled: true, trigger: null, ...graph } as WorkflowBuildRequest);
      // Private on the component — the save itself (validate → PUT) is not what's under test here.
      const saved = spyOn(builder as unknown as { saveWorkflow: () => void }, 'saveWorkflow');
      builder.onSave();
      return { saved };
    }

    it('refuses a chain no source reaches — it would run on nothing, and report success', () => {
      spyOn(toast, 'error');
      // The MPI (disabled) is cut off from Mapping, so nothing feeds Transformation → De-identification → Mapping.
      const { saved } = attemptSave({
        nodes: [runNode('Epic', 0), runNode('MPI', 10, false), runNode('Transformation', 10),
          runNode('De-identification', 10), runNode('Mapping', 10), runNode('SQL Server', 20)],
        edges: [
          { fromNodeId: 'Transformation', toNodeId: 'De-identification' },
          { fromNodeId: 'De-identification', toNodeId: 'Mapping' },
          { fromNodeId: 'Mapping', toNodeId: 'SQL Server' },
        ],
      });

      expect(saved).not.toHaveBeenCalled();
      expect(toast.error).toHaveBeenCalledWith(
        'Pipeline not connected',
        jasmine.stringMatching(/^Transformation, De-identification, Mapping, SQL Server are not connected to a source/),
      );
    });

    it('lets a pipeline every step of which a source reaches go through to the save', () => {
      const { saved } = attemptSave({
        nodes: [runNode('Epic', 0), runNode('MPI', 10, false), runNode('Mapping', 10), runNode('SQL Server', 20)],
        edges: [{ fromNodeId: 'Epic', toNodeId: 'Mapping' }, { fromNodeId: 'Mapping', toNodeId: 'SQL Server' }],
      });

      expect(saved).toHaveBeenCalled();
    });
  });
});
