import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { WORKFLOW_ENDPOINTS } from '../core/api-endpoints';

export type WorkflowNodeCategory = 0 | 10 | 20 | 30 | 40;
export type WorkflowDataContract =
  | 'None'
  | 'ResourceBatch'
  | 'NormalizedResourceBatch'
  | 'MappedRecordBatch'
  | 'DeIdentifiedBatch'
  | 'DestinationWriteResult'
  | 'AuditResult'
  | number;

export interface WorkflowCatalogItem {
  nodeType: string;
  category: WorkflowNodeCategory | string;
  rank: number;
  requiredConfigurationFields: string[];
  inputContracts: WorkflowDataContract[];
  outputContract: WorkflowDataContract;
  executorKey: string;
  displayName?: string;
  transformId?: string;
  description?: string;
}

export interface WorkflowNodeRequest {
  id: string;
  nodeType: string;
  category: WorkflowNodeCategory | string;
  rank: number;
  subRank: number;
  displayName: string | null;
  configurationJson: string | null;
  positionX: number;
  positionY: number;
  isEnabled: boolean;
}

export interface WorkflowEdgeRequest {
  fromNodeId: string;
  toNodeId: string;
}

export interface WorkflowDefinitionRequest {
  name: string;
  isEnabled: boolean;
  nodes: WorkflowNodeRequest[];
  edges: WorkflowEdgeRequest[];
}

export interface WorkflowNodeDto extends WorkflowNodeRequest {
  id: string;
  configuration?: Record<string, string>[];
}

export interface WorkflowEdgeDto {
  id: string;
  fromNodeId: string;
  toNodeId: string;
}

export interface WorkflowDefinitionDto {
  id: string;
  name: string;
  version: number;
  isEnabled: boolean;
  isActive?: boolean;
  nodes: WorkflowNodeDto[];
  edges: WorkflowEdgeDto[];
}

export interface WorkflowValidationResult {
  isValid: boolean;
  errors: string[];
}

export interface WorkflowRunDto {
  id: string;
  status?: string;
  startedAt?: string;
  completedAt?: string | null;
  nodeRuns?: unknown[];
}

export interface WorkflowRunResultDto {
  workflowRun?: WorkflowRunDto;
  run?: WorkflowRunDto;
}

@Injectable({ providedIn: 'root' })
export class WorkflowApiService {
  private readonly http = inject(HttpClient);
  private readonly catalogItems = signal<WorkflowCatalogItem[]>([]);

  readonly catalog = this.catalogItems.asReadonly();

  loadCatalog(): Observable<WorkflowCatalogItem[]> {
    return this.http.get<WorkflowCatalogItem[]>(WORKFLOW_ENDPOINTS.catalog).pipe(
      tap(items => this.catalogItems.set(items)),
    );
  }

  validate(request: WorkflowDefinitionRequest): Observable<WorkflowValidationResult> {
    return this.http.post<WorkflowValidationResult>(WORKFLOW_ENDPOINTS.validate, request);
  }

  save(request: WorkflowDefinitionRequest, workflowId?: string | null): Observable<WorkflowDefinitionDto> {
    return workflowId
      ? this.http.put<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.byId(workflowId), request)
      : this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.create, request);
  }

  load(workflowId: string): Observable<WorkflowDefinitionDto> {
    return this.http.get<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.byId(workflowId));
  }

  run(workflowId: string): Observable<WorkflowRunResultDto> {
    return this.http.post<WorkflowRunResultDto>(WORKFLOW_ENDPOINTS.run(workflowId), {});
  }

  runs(workflowId: string): Observable<WorkflowRunDto[]> {
    return this.http.get<WorkflowRunDto[]>(WORKFLOW_ENDPOINTS.runs(workflowId));
  }

  activate(workflowId: string): Observable<WorkflowDefinitionDto> {
    return this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.activate(workflowId), {});
  }
}
