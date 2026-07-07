import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
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

// ── Option B create-on-save (POST /workflows/build) ────────────────────────────
// Enum-valued fields are sent as backend enum NAMES (the API accepts names or numbers).
export interface SourceAuthenticationRequest {
  authenticationType: string;                 // None | SmartBackendServices | OAuthClientCredentials | ApiKey
  clientId?: string | null;
  tokenEndpoint?: string | null;
  scopes?: string[];
  clientSecretKeyVaultName?: string | null;
  clientSecretName?: string | null;
  privateKeyKeyVaultName?: string | null;
  privateKeySecretName?: string | null;
  keyId?: string | null;
}

export interface SourceInteractiveConfigurationRequest {
  redirectUris: string[];
  launchUrl?: string | null;
  trustedIssuers?: string[];
  patientSelectionMethod?: string | null;
}

export interface CreateSourceConnectionRequest {
  name: string;
  sourceSystemType: string;                   // Sample | Epic | ...
  baseUrl: string;
  authentication: SourceAuthenticationRequest;
  applicationType?: string | null;           // Backend | EhrLaunch | Standalone | Patient
  interactive?: SourceInteractiveConfigurationRequest | null;
}

export interface CreateDestinationConfigurationRequest {
  name: string;
  destinationType: string;                     // SqlServer | Csv | Sftp | ...
  keyVaultName: string;
  secretName: string;
  target?: string | null;
  inlineSecret?: string | null;               // raw connstr / sftp:// URI — provisioned encrypted server-side
}

export interface MappingFieldRequest {
  targetField: string;
  jsonPath: string;
  valueType: string;                           // String | Integer | Decimal | Boolean | Date | DateTime | Json
  isRequired: boolean;
  defaultValue?: string | null;
  format?: string | null;
}

export interface SourceBuildSpec { nodeId: string; source: CreateSourceConnectionRequest; }
export interface DestinationBuildSpec { nodeId: string; destination: CreateDestinationConfigurationRequest; }
export interface MappingBuildSpec {
  nodeId: string;
  sourceNodeId: string;
  destinationNodeId: string;
  name: string;
  resourceType: string;
  destinationObject: string;
  fields: MappingFieldRequest[];
}

export interface WorkflowBuildRequest {
  name: string;
  isEnabled: boolean;
  nodes: WorkflowNodeRequest[];
  edges: WorkflowEdgeRequest[];
  sources?: SourceBuildSpec[];
  destinations?: DestinationBuildSpec[];
  mappings?: MappingBuildSpec[];
}

export interface WorkflowBuildResult {
  workflowId: string;
  sourceConnectionIds: Record<string, string>;
  destinationIds: Record<string, string>;
  mappingProfileIds: Record<string, string>;
}

// ── Workflow-list screen (GET /workflows/summary) ──────────────────────────────
export type WorkflowAction = 'Launch' | 'Run';

export interface WorkflowSummary {
  workflowId: string;
  name: string;
  status: 'Enabled' | 'Disabled';
  nodes: number;
  edges: number;
  lastRun: string | null;           // WorkflowRunStatus name (Running | Succeeded | Failed) or null
  lastRunAt: string | null;
  action: WorkflowAction;
  actionEndpoint: string;
  sourceConnectionId: string | null;
  sourceSystemType: string | null;  // Epic | Cerner | Sample | ...
  applicationType: string | null;   // Backend | EhrLaunch | Standalone | Patient
  hasDestination: boolean;
}

export interface WorkflowLaunchUrl {
  launchUrl: string;
}

/** A capped sample of rows read back from a relational destination table ("View destination data"). */
export interface DestinationData {
  table: string;
  columns: string[];
  rows: (string | null)[][];
  rowCount: number;
  error: string | null;
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

  /** Option B create-on-save: provisions secrets + creates Source/Destination/Mapping and saves the graph in one call. */
  build(request: WorkflowBuildRequest): Observable<WorkflowBuildResult> {
    return this.http.post<WorkflowBuildResult>(WORKFLOW_ENDPOINTS.build, request);
  }

  /** Workflow-list screen: one summary row per workflow with the derived Launch/Run action. */
  summary(): Observable<WorkflowSummary[]> {
    return this.http.get<WorkflowSummary[]>(WORKFLOW_ENDPOINTS.summary);
  }

  run(workflowId: string): Observable<WorkflowRunResultDto> {
    return this.http.post<WorkflowRunResultDto>(WORKFLOW_ENDPOINTS.run(workflowId), {});
  }

  /** Interactive (EHR launch / standalone / patient) workflows: the opaque launch URL to register with the EHR. */
  launchUrl(workflowId: string): Observable<WorkflowLaunchUrl> {
    return this.http.get<WorkflowLaunchUrl>(WORKFLOW_ENDPOINTS.launchUrl(workflowId));
  }

  /** Reads back a capped sample of rows the workflow's destination wrote. */
  destinationData(workflowId: string, top = 50): Observable<DestinationData> {
    return this.http.get<DestinationData>(
      WORKFLOW_ENDPOINTS.destinationData(workflowId),
      { params: new HttpParams().set('top', top) },
    );
  }

  runs(workflowId: string): Observable<WorkflowRunDto[]> {
    return this.http.get<WorkflowRunDto[]>(WORKFLOW_ENDPOINTS.runs(workflowId));
  }

  activate(workflowId: string): Observable<WorkflowDefinitionDto> {
    return this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.activate(workflowId), {});
  }

  deactivate(workflowId: string): Observable<WorkflowDefinitionDto> {
    return this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.deactivate(workflowId), {});
  }

  delete(workflowId: string): Observable<void> {
    return this.http.delete<void>(WORKFLOW_ENDPOINTS.byId(workflowId));
  }
}
