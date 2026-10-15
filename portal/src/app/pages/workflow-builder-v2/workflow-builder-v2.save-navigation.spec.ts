import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { WorkflowBuilderV2Component } from './workflow-builder-v2.component';
import { TransformationRulesService } from '../../components/node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { DeIdentificationProfileService } from '../../destination-connections/services/deidentification-profile.service';
import { WorkflowApiService } from '../../services/workflow-api.service';
import { WorkflowGraphMapperServiceV2 } from '../../services/workflow-graph-mapper-v2.service';
import { WorkflowBuildAssemblerServiceV2 } from '../../services/workflow-build-assembler-v2.service';
import { PermissionService } from '../../auth/services/permission.service';
import { ToastService } from '../../services/toast.service';

/**
 * After a Save the builder returns to the Workflows list. It carries the saved workflow's name and id along so the
 * list arrives already searched for it with its row highlighted — otherwise it is easy to lose in a long list.
 */
describe('WorkflowBuilderV2 — returns to a list filtered to the saved workflow', () => {
  let router: Router;
  let builder: {
    finishSave(): void;
    workflowName: { set(name: string): void };
    currentWorkflowId: { set(id: string | null): void };
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: TransformationRulesService, useValue: { list: () => of([]) } },
        { provide: DeIdentificationProfileService, useValue: { list: () => of([]) } },
        { provide: WorkflowApiService, useValue: { catalog: () => [], loadCatalog: () => of([]) } },
        { provide: WorkflowGraphMapperServiceV2, useValue: {} },
        { provide: WorkflowBuildAssemblerServiceV2, useValue: {} },
        { provide: PermissionService, useValue: { hasPermission: () => true, hasAny: () => true } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show', 'success', 'error', 'warning']) },
      ],
    });
    TestBed.overrideComponent(WorkflowBuilderV2Component, { set: { template: '', imports: [] } });
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    builder = TestBed.createComponent(WorkflowBuilderV2Component).componentInstance as unknown as typeof builder;
  });

  it('passes the saved workflow name and id to the list', () => {
    builder.workflowName.set('  Epic-Backend-SQL  ');
    builder.currentWorkflowId.set('wf-123');

    builder.finishSave();

    expect(router.navigate).toHaveBeenCalledWith(['/workflows'], {
      queryParams: { search: 'Epic-Backend-SQL', highlight: 'wf-123' },
    });
  });

  it('omits the params it does not have rather than sending empty ones', () => {
    builder.workflowName.set('');
    builder.currentWorkflowId.set(null);

    builder.finishSave();

    expect(router.navigate).toHaveBeenCalledWith(['/workflows'], {
      queryParams: { search: null, highlight: null },
    });
  });
});
