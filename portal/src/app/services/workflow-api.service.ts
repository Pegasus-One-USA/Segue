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
  /** Optional multi-line free-text notes shown next to the name. Null/blank clears whatever was stored. */
  description?: string | null;
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
  /** IANA time zone (e.g. "America/New_York") the schedule is evaluated in. Defaults to "UTC" server-side. */
  timeZoneId?: string | null;
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
  description?: string | null;
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

/** Response for both the async /run's 202 Accepted and GET /workflow-runs/{runId}/status. */
export interface WorkflowRunStatus {
  workflowRunId: string;
  status: 'Running' | 'Succeeded' | 'Failed' | string;
  correlationId?: string | null;
}

// ── Option B create-on-save (POST /workflows/build) ────────────────────────────
// Enum-valued fields are sent as backend enum NAMES (the API accepts names or numbers).
export interface SourceAuthenticationRequest {
  authenticationType: string;                 // None | SmartBackendServices | OAuthClientCredentials | ApiKey
  clientId?: string | null;
  tokenEndpoint?: string | null;
  /** The SMART authorization (browser redirect) endpoint resolved by the wizard's "Discover"
   *  (SourceAuthenticationDto.AuthorizationEndpoint). Persisted so re-opening a saved connection shows back the
   *  URL it was actually configured with; the interactive sign-in flow still re-discovers this live at authorize
   *  time. Null for a non-interactive (client_credentials) app, which never uses one. */
  authorizationEndpoint?: string | null;
  scopes?: string[];
  clientSecretKeyVaultName?: string | null;
  clientSecretName?: string | null;
  privateKeyKeyVaultName?: string | null;
  privateKeySecretName?: string | null;
  keyId?: string | null;
  /** The URL actually registered with the EHR to fetch this connection's JWK Set (SourceAuthenticationDto.JwksUrl).
   *  Purely informational — FHIRBridge never fetches it — but eCW additionally requires this URL's HOST to be
   *  allow-listed on its own servers, so persisting what was really registered is what makes a later bare
   *  `invalid_client` diagnosable. Null for connections that don't sign a JWT assertion. */
  jwksUrl?: string | null;
  /** Scopes Epic (or another EHR) actually granted on the last successful Discover token exchange — distinct
   *  from `scopes` (what was requested). Null until Discover has run once. */
  discoveredScopes?: string[] | null;
  /** athenahealth only — the bare numeric practice id (e.g. "195900") the backend builds the
   *  ah-practice=Organization/a-1.Practice-{id} reference from. Null for every other vendor. */
  practiceId?: string | null;
  /** A wizard-typed raw client secret to provision at (clientSecretKeyVaultName, clientSecretName) — mirrors
   *  CreateDestinationConfigurationRequest.inlineSecret. Null when the user didn't (re)type one (an unedited
   *  existing connection keeps whatever secret is already stored at that reference). */
  inlineClientSecret?: string | null;
  /** Where OAuth2ClientCredentialsTokenProvider places client id/secret — "post" (default) or "basic". Only
   *  meaningful for Client Secret auth. */
  authPlacement?: 'post' | 'basic' | null;
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
  // Set when forking a brand-new connection off an existing one the user picked but then edited — lets the
  // backend resolve that destination's own stored secret and inherit its credentials into inlineSecret when
  // the latter is missing them (e.g. only "Require SSL" changed, never the password). SQL-family only.
  inheritSecretFromDestinationId?: string | null;
}

export interface MappingFieldRequest {
  targetField: string;
  jsonPath: string;
  valueType: string;                           // String | Integer | Decimal | Boolean | Date | DateTime | Json
  isRequired: boolean;
  defaultValue?: string | null;
  format?: string | null;
  arrayPolicy?: string;                        // Scalar | FirstItem | RepeatParent | SeparateDestination | StoreJson | RejectIfMultiple | CorrelateByCode
  arrayAncestors?: string[] | null;            // array-ancestor fhir paths (child-table alignment)
  isUpsertKey?: boolean;                       // marks the column an Upsert write matches an existing row on
  // Required when arrayPolicy is CorrelateByCode: picks the array item whose sibling code element (an absolute
  // JsonPath sharing this field's array ancestor, e.g. "$.component[*].code.coding[*].code") equals
  // correlationCodeValue (e.g. "8480-6" for a blood-pressure Observation's systolic component), instead of taking
  // items by position. Ignored for every other arrayPolicy.
  correlationCodeJsonPath?: string | null;
  correlationCodeValue?: string | null;
  // Mirrors the remaining MappingFieldDto members (docs/backend/14-mapping-profile-master-screen-plan.md §3.2) —
  // MaxLength/Precision/Scale are deliberately excluded, since the backend documents them as never persisted on
  // the profile itself, only filled in at pipeline run time from the destination's live schema.
  normalizationType?: string | null;
  terminologySystemJsonPath?: string | null;
  terminologyCodeJsonPath?: string | null;
  cardinality?: string | null;
  isEnabled?: boolean;
  // Child-table (arrayPolicy: SeparateDestination) support — mirrors MappingFieldDto's own members of the
  // same name. Only set when this field's real destination table differs from the profile's own
  // destinationObject (e.g. a Patient.name array fanned out into a separate dbo.PatientName table); absent
  // for every ordinary same-table field, matching today's wire shape exactly.
  destinationObject?: string | null;
  parentTable?: string | null;
  parentKeyColumn?: string | null;
  foreignKeyColumn?: string | null;
  // FK-aware reference resolution — set when this field's source is a FHIR reference (e.g. "$.subject.
  // reference") that must be resolved against another mapped resource's own table + id column at write time,
  // rather than written verbatim (a bigint FK column can never accept a raw "Patient/xyz" string). Mirrors
  // MappingFieldDto.ReferenceLookupTable/ReferenceLookupKeyColumn on the backend exactly.
  referenceLookupTable?: string | null;
  referenceLookupKeyColumn?: string | null;
}

// existingId: when the node already carries an id from a prior create-on-save (round-tripped through node.fields on
// load-and-edit), the server updates that record in place instead of provisioning a duplicate.
export interface SourceBuildSpec { nodeId: string; source: CreateSourceConnectionRequest; existingId?: string | null; }
export interface DestinationBuildSpec { nodeId: string; destination: CreateDestinationConfigurationRequest; existingId?: string | null; }
// Declares this spec's resource as a "child" of another resource on the SAME destination (matched by
// parentResourceType against a sibling MappingBuildSpec sharing destinationNodeId) — e.g. Observation
// declaring Patient as a parent requires "subject.reference" to be mapped. Validated server-side in
// /workflows/build before anything is created; see WorkflowEndpoints.ValidateMappingParentReferences.
export interface ParentReferenceSpec {
  parentResourceType: string;
  referenceFieldOverride?: string | null;
}

export interface MappingBuildSpec {
  nodeId: string;
  sourceNodeId: string;
  destinationNodeId: string;
  name: string;
  resourceType: string;
  destinationObject: string;
  fields: MappingFieldRequest[];
  existingId?: string | null;
  parentReferences?: ParentReferenceSpec[];
}

export interface WorkflowBuildRequest {
  name: string;
  description?: string | null;
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
  /** Free-text notes captured in the builder; null/absent when never filled in. */
  description?: string | null;
  status: 'Enabled' | 'Disabled';
  nodes: number;
  edges: number;
  lastRun: string | null;           // WorkflowRunStatus name (Running | Succeeded | Failed) or null
  lastRunAt: string | null;
  /** Populated live via RunStatusHub when lastRun becomes Failed (see WorkflowListComponent's constructor) —
   *  not part of the initial /workflows/summary fetch, so it's absent until a failure event actually arrives
   *  during this session. Null/absent means "no reference id to show," never a placeholder. */
  lastRunErrorReferenceId?: string | null;
  action: WorkflowAction;
  actionEndpoint: string;
  sourceConnectionId: string | null;
  sourceSystemType: string | null;  // Epic | Cerner | Sample | ...
  applicationType: string | null;   // Backend | EhrLaunch | Standalone | Patient
  hasDestination: boolean;
  isPubliclyLaunchable: boolean;
  createdOnUtc?: string | null;
  createdBy?: string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?: string | null;
}

/** Server-side page of /workflows/summary — items is just this page's rows, totalCount is the full matching-row
 *  count (before paging) for the "Showing X-Y of Z" / page-count UI. The three available* lists are the full
 *  distinct-value set across every workflow (not just what matches the active filters), for the Status/Audience/
 *  Source multi-select filter checkboxes' option lists. */
export interface WorkflowSummaryPage {
  items: WorkflowSummary[];
  totalCount: number;
  availableStatuses: string[];
  availableApplicationTypes: string[];
  availableSourceSystemTypes: string[];
}

export interface WorkflowSummaryQuery {
  page: number;
  pageSize: number;
  search?: string;
  sortColumn?: string;
  sortDirection?: 'asc' | 'desc';
  statuses?: string[];
  applicationTypes?: string[];
  sourceSystemTypes?: string[];
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

  /** Workflow-list screen: one summary row per workflow with the derived Launch/Run action. Paging/search/sort are
   *  applied server-side — see WorkflowEndpoints.MapGet("/workflows/summary"). */
  summary(query: WorkflowSummaryQuery): Observable<WorkflowSummaryPage> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
    if (query.search) params = params.set('search', query.search);
    if (query.sortColumn) params = params.set('sortColumn', query.sortColumn);
    if (query.sortDirection) params = params.set('sortDirection', query.sortDirection);
    for (const value of query.statuses ?? []) params = params.append('statuses', value);
    for (const value of query.applicationTypes ?? []) params = params.append('applicationTypes', value);
    for (const value of query.sourceSystemTypes ?? []) params = params.append('sourceSystemTypes', value);
    return this.http.get<WorkflowSummaryPage>(WORKFLOW_ENDPOINTS.summary, { params });
  }

  /**
   * `async: true` returns as soon as the run is accepted (202, with the run id) instead of blocking until the
   * whole DAG finishes — use with pollRunStatus so a long run survives navigating away from this screen.
   */
  run(workflowId: string, async = false): Observable<WorkflowRunResultDto | WorkflowRunStatus> {
    return this.http.post<WorkflowRunResultDto | WorkflowRunStatus>(
      WORKFLOW_ENDPOINTS.run(workflowId),
      async ? { async: true } : {},
    );
  }

  /** Poll target for an async run: 'Running' until the orchestrator persists a terminal status. */
  runStatus(workflowRunId: string): Observable<WorkflowRunStatus> {
    return this.http.get<WorkflowRunStatus>(WORKFLOW_ENDPOINTS.runStatus(workflowRunId));
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
  copy(workflowId: string, name: string, description?: string | null): Observable<WorkflowDefinitionDto> {
    // description omitted (undefined) → the server keeps the original's description on the copy.
    return this.http.post<WorkflowDefinitionDto>(
      WORKFLOW_ENDPOINTS.copy(workflowId),
      description === undefined ? { name } : { name, description });
  }
}
