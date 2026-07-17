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
  checkpointUrlEnabled?: boolean;
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
  trigger?: WorkflowTriggerRequest | null;
  isPubliclyLaunchable?: boolean;
}

/** Workflow-level schedule (Backend-Systems workflows). Omit / Manual = run on demand. Matches the backend DTO. */
export interface WorkflowTriggerRequest {
  type: 'Manual' | 'Schedule' | 'Poll';
  scheduleExpression?: string | null;
  intervalMinutes?: number | null;
  backfillOnFirstRun?: boolean;
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
  isPubliclyLaunchable?: boolean;
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
  /** How the app is registered to open within the EHR (EHR-launch audience only) — 'Embedded' | 'ExternalBrowser' | 'Sidebar'. */
  launchDisplayMode?: string | null;
}

export interface SourceRetrievalConfigurationRequest {
  retrievalMethod: string;                    // search-rest | subscription | webhook | bulk-export
  resourceTypes: string[];
  searchCriteria?: string | null;
  incrementalSyncEnabled?: boolean;
  pageSize?: number | null;
  sortOrder?: string | null;
  includeParameters?: string[] | null;
  revIncludeParameters?: string[] | null;
  retryPolicy?: string | null;
  timeoutSeconds?: number | null;
  maxRecordsPerRun?: number | null;
  // Bulk Data $export (retrievalMethod === 'bulk-export') only.
  exportScope?: string | null;                // system | patient | group
  groupId?: string | null;                    // required when exportScope === 'group'
  patientIds?: string[] | null;               // narrows a patient-scoped export; empty = all patients
  outputFormat?: string | null;               // e.g. application/fhir+ndjson
}

export interface CreateSourceConnectionRequest {
  name: string;
  sourceSystemType: string;                   // Sample | Epic | ...
  baseUrl: string;
  authentication: SourceAuthenticationRequest;
  applicationType?: string | null;           // Backend | EhrLaunch | Standalone | Patient
  interactive?: SourceInteractiveConfigurationRequest | null;
  retrieval?: SourceRetrievalConfigurationRequest | null;
}

export interface CreateDestinationConfigurationRequest {
  name: string;
  destinationType: string;                     // SqlServer | Csv | Sftp | ...
  keyVaultName: string;
  secretName: string;
  target?: string | null;
  inlineSecret?: string | null;               // raw connstr / sftp:// URI — provisioned encrypted server-side
  connectionMetadataJson?: string | null;     // non-secret dest_* fields, JSON — lets a later "existing" pick repopulate
}

export interface MappingFieldRequest {
  targetField: string;
  jsonPath: string;
  valueType: string;                           // String | Integer | Decimal | Boolean | Date | DateTime | Json
  isRequired: boolean;
  defaultValue?: string | null;
  format?: string | null;
  arrayPolicy?: string;                        // Scalar | FirstItem | RepeatParent | SeparateDestination | StoreJson | RejectIfMultiple
  arrayAncestors?: string[] | null;            // array-ancestor fhir paths (child-table alignment)
}

// existingId: when the node already carries an id from a prior create-on-save (round-tripped through node.fields on
// load-and-edit), the server updates that record in place instead of provisioning a duplicate.
export interface SourceBuildSpec { nodeId: string; source: CreateSourceConnectionRequest; existingId?: string | null; }
export interface DestinationBuildSpec { nodeId: string; destination: CreateDestinationConfigurationRequest; existingId?: string | null; }
export interface MappingBuildSpec {
  nodeId: string;
  sourceNodeId: string;
  destinationNodeId: string;
  name: string;
  resourceType: string;
  destinationObject: string;
  fields: MappingFieldRequest[];
  existingId?: string | null;
}

export interface WorkflowBuildRequest {
  name: string;
  isEnabled: boolean;
  nodes: WorkflowNodeRequest[];
  edges: WorkflowEdgeRequest[];
  sources?: SourceBuildSpec[];
  destinations?: DestinationBuildSpec[];
  mappings?: MappingBuildSpec[];
  trigger?: WorkflowTriggerRequest | null;
  // Set when re-saving an already-built workflow: the server updates that workflow definition (and, paired with each
  // spec's existingId, the Source/Destination/MappingProfile records) in place instead of creating duplicates.
  workflowId?: string | null;
}

export interface WorkflowBuildResult {
  workflowId: string;
  sourceConnectionIds: Record<string, string>;
  destinationIds: Record<string, string>;
  mappingProfileIds: Record<string, string>;
  // Keyed by source connection id (as a string) — each connection's Epic OAuth scopes as they stand right after
  // this save, derived from every pipeline's destination resource selections (not just this one). Absent for any
  // connection that isn't interactive (e.g. Backend Services), since those aren't resource-picker driven.
  syncedScopesBySourceConnectionId?: Record<string, string[]>;
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
  isPubliclyLaunchable: boolean;
}

export interface WorkflowLaunchUrl {
  launchUrl: string;
  /** 'ehr-launch' → register in the EHR (invoked by it); 'standalone' | 'patient' → opened directly. */
  mode?: 'ehr-launch' | 'standalone' | 'patient';
  /** True when the URL is opened directly by a user; false when the EHR invokes it with iss + launch. */
  opensDirectly?: boolean;
}

/** A per-node "Copy URL" checkpoint (Phase 1) — running it executes only that node's ancestor closure. */
export interface WorkflowCheckpointUrl {
  checkpointUrl: string;
}

export interface WorkflowCheckpointResult {
  result: unknown;
  contract: string | null;
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

  /** Per-node checkpoint (Phase 1): the node must already have checkpointUrlEnabled saved server-side. */
  checkpointUrl(workflowId: string, nodeId: string): Observable<WorkflowCheckpointUrl> {
    return this.http.get<WorkflowCheckpointUrl>(WORKFLOW_ENDPOINTS.checkpointUrl(workflowId, nodeId));
  }

  /** Reads back a checkpoint run's captured node output, resolved from just the run id. */
  checkpointResult(workflowRunId: string): Observable<WorkflowCheckpointResult> {
    return this.http.get<WorkflowCheckpointResult>(WORKFLOW_ENDPOINTS.checkpointResult(workflowRunId));
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

  /** Opts the workflow into the anonymous public-standalone-url mint endpoint a third-party app's hospital picker
   *  calls (see OAuthController.GetPublicWorkflowStandaloneUrl) — required before that endpoint honors it. */
  enablePublicLaunch(workflowId: string): Observable<WorkflowDefinitionDto> {
    return this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.enablePublicLaunch(workflowId), {});
  }

  disablePublicLaunch(workflowId: string): Observable<WorkflowDefinitionDto> {
    return this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.disablePublicLaunch(workflowId), {});
  }

  delete(workflowId: string): Observable<void> {
    return this.http.delete<void>(WORKFLOW_ENDPOINTS.byId(workflowId));
  }

  /** Duplicates a workflow (exact node/edge/config copy, new ids) under a new name. Always created disabled. */
  copy(workflowId: string, name: string): Observable<WorkflowDefinitionDto> {
    return this.http.post<WorkflowDefinitionDto>(WORKFLOW_ENDPOINTS.copy(workflowId), { name });
  }
}
