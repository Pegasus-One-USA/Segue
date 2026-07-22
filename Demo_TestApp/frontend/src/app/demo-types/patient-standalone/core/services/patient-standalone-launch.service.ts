import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../../../environments/environment';

// The FHIRBridge base URL and both workflow ids (list + detail) are resolved once via loadConfig() from the
// admin-configured settings (see WorkflowSettingsEntity.PatientWorkflowId/PatientDetailWorkflowId/PatientBaseUrl) —
// formerly a gitignored per-developer local file (standalone-launch.config.ts). The token/run/mint/discard calls
// below still take an explicit workflowId parameter (rather than always using one resolved field), since the list
// fetch and the per-patient detail fetch run against two different workflows (see launch-standalone-patient.ts).

/** Matches FHIRBridge's PublicEhrEpicEndpointDto shape (GET /api/v1/ehr-mychart-endpoints) — anonymous,
 *  EndpointType.MyChart rows only (a specific customer/hospital's own branded production instance, never Epic's
 *  shared sandbox — that stays behind Provider Standalone's own picker). */
export interface MyChartEndpoint {
  id: string;
  name: string;
  fhirBaseUrl: string;
  status: string;
}

/** Matches PatientStandaloneLaunchController's response shape. */
export interface PublicPatientStandaloneUrlResponse {
  launchUrl: string;
  mode: string;
  opensDirectly: boolean;
  applicationType: string | null;
}

/** Matches WorkflowEndpoints' GET /workflows/{id}/token-status — a cheap, no-pipeline check of whether a real /run
 *  would currently succeed authentication-wise. */
export interface TokenStatusResponse {
  hasValidToken: boolean;
}

export interface LaunchResultResponse {
  patient: unknown;
  patientId: string | null;
}

// Matches RankedWorkflowOrchestrator's result shape — a 200 OK here is always a fully Succeeded run; any node/token
// failure throws past the orchestrator before it ever returns, so it always lands in the caller's catch block
// instead as an HttpErrorResponse with a bare {error: "..."} body.
export interface WorkflowRunResponse {
  workflowRun: {
    status: string;
    errorMessage: string | null;
  };
  outputsByNodeId?: Record<string, {
    nodeType: string;
    payload?: { resources?: Array<{ resourceType: string; resourceId: string; payload: string }> };
    // Only a CSV destination node using Download-URL delivery populates downloadUrl (see
    // DestinationNodeExecutor.ExecuteAsync) — absent for every other node type/delivery mode.
    metadata?: { downloadUrl?: string | null };
  }>;
}

export interface FetchedPatient {
  id: string;
  name: string;
  birthDate: string | null;
}

export interface ObservationDetail {
  id: string;
  effectiveDateTime: string | null;
  status: string | null;
  codeDisplay: string | null;
}

export interface ConditionDetail {
  codeDisplay: string | null;
  subjectReference: string | null;
  resourceType: string;
}

export interface PatientDetail {
  id: string;
  firstName: string | null;
  birthDate: string | null;
  gender: string | null;
  observations: ObservationDetail[];
  conditions: ConditionDetail[];
}

/** Matches Demo_TestApp/backend's GET /api/epic-session/status — HealthApp's own record, not FHIRBridge's. See
 *  patient-standalone-launch.service.ts's rememberSession/forgetSession for why this deliberately doesn't hold a
 *  real Epic/MyChart token, just a "last known good" signal. */
export interface EpicSessionStatusResponse {
  hasSession: boolean;
  patientId: string | null;
  workflowId: string | null;
  lastConfirmedValidUtc: string | null;
}

/** Matches Demo_TestApp/backend's GET /api/patient-standalone-settings — the admin-configured FHIRBridge workflow
 *  ids + base URL for this flow (see WorkflowSettingsEntity.PatientWorkflowId/PatientDetailWorkflowId/PatientBaseUrl),
 *  formerly a gitignored per-developer local file (standalone-launch.config.ts). workflowId is the list fetch's
 *  workflow; detailWorkflowId is the separate workflow used only for the per-patient detail fetch.
 *  csvExportWorkflowId/csvEmailExportWorkflowId back the "Download Patient Information"/"Email Patient Information"
 *  buttons — formerly the hardcoded CSV_EXPORT_WORKFLOW_ID/CSV_EMAIL_EXPORT_WORKFLOW_ID constants in
 *  standalone-launch.config.ts, now admin-configurable via WorkflowSettingsEntity.PatientCsvExportWorkflowId/
 *  PatientCsvEmailExportWorkflowId. */
export interface PatientStandaloneSettingsResponse {
  workflowId: string;
  detailWorkflowId: string;
  baseUrl: string;
  csvExportWorkflowId: string;
  csvEmailExportWorkflowId: string;
}

// HealthApp's own backend (Demo_TestApp), not FHIRBridge — remembers which patient/workflow this HealthApp user
// last launched, centrally, without requiring any FHIRBridge change. Same mechanism Provider Standalone already
// reuses (EpicSessionStore is keyed purely by HealthApp userId, so a distinct Patient Standalone demo login has its
// own row with no collision). Also now the source of the FHIRBridge workflow ids + base URL themselves (see
// loadConfig) — previously a gitignored local file, now an admin-configurable setting.
const HEALTHAPP_BACKEND_BASE_URL = environment.healthAppBase;

/** Pulls the displayable Patient rows out of a /run response's raw Source node output. A Patient Standalone launch
 *  typically resolves to exactly one patient (the signed-in user), but this stays list-shaped in case more than one
 *  Patient resource ever comes back. */
export function extractFetchedPatients(result: WorkflowRunResponse): FetchedPatient[] {
  const sourceOutput = Object.values(result.outputsByNodeId ?? {}).find(output => output.nodeType === 'EpicSourceNode');
  const resources = sourceOutput?.payload?.resources ?? [];
  return resources
    .filter(resource => resource.resourceType === 'Patient')
    .map((resource): FetchedPatient => {
      let name = '(no name on file)';
      let birthDate: string | null = null;
      try {
        const parsed = JSON.parse(resource.payload) as {
          name?: Array<{ text?: string; family?: string; given?: string[] }>;
          birthDate?: string;
        };
        const nameEntry = parsed.name?.[0];
        name = nameEntry?.text
          || [nameEntry?.given?.join(' '), nameEntry?.family].filter(Boolean).join(' ')
          || name;
        birthDate = parsed.birthDate ?? null;
      } catch {
        // Malformed payload for this one resource — still list it by id rather than dropping it silently.
      }
      return { id: resource.resourceId, name, birthDate };
    });
}

// FHIR references are typically "Patient/{id}", occasionally a full URL ending the same way — match on the
// trailing segment rather than requiring an exact "Patient/{id}" string.
function referenceMatchesPatientId(reference: string | undefined, patientId: string): boolean {
  return !!reference && reference.split('/').pop() === patientId;
}

/** Pulls this patient's own Patient/Observation/Condition resources out of a patient-scoped /run response (see
 *  fetchPatientDetail — this is called with patientId already fixed to the clicked list row, so the source node's
 *  own resources are already scoped to that one patient; the subject-reference filter below is still applied
 *  defensively rather than assumed). Returns null if the response didn't include a matching Patient resource at
 *  all (e.g. a malformed or empty run). */
export function extractPatientDetail(result: WorkflowRunResponse, patientId: string): PatientDetail | null {
  const sourceOutput = Object.values(result.outputsByNodeId ?? {}).find(output => output.nodeType === 'EpicSourceNode');
  const resources = sourceOutput?.payload?.resources ?? [];

  const patientResource = resources.find(resource => resource.resourceType === 'Patient' && resource.resourceId === patientId)
    ?? resources.find(resource => resource.resourceType === 'Patient');
  if (!patientResource) {
    return null;
  }

  let firstName: string | null = null;
  let birthDate: string | null = null;
  let gender: string | null = null;
  try {
    const parsed = JSON.parse(patientResource.payload) as {
      name?: Array<{ given?: string[] }>;
      birthDate?: string;
      gender?: string;
    };
    firstName = parsed.name?.[0]?.given?.join(' ') ?? null;
    birthDate = parsed.birthDate ?? null;
    gender = parsed.gender ?? null;
  } catch {
    // Malformed Patient payload — still show the Observation/Condition sections below with whatever parsed.
  }

  const observations: ObservationDetail[] = resources
    .filter(resource => resource.resourceType === 'Observation')
    .map((resource): ObservationDetail | null => {
      try {
        const parsed = JSON.parse(resource.payload) as {
          subject?: { reference?: string };
          effectiveDateTime?: string;
          status?: string;
          code?: { coding?: Array<{ display?: string }> };
        };
        if (!referenceMatchesPatientId(parsed.subject?.reference, patientId)) {
          return null;
        }
        return {
          id: resource.resourceId,
          effectiveDateTime: parsed.effectiveDateTime ?? null,
          status: parsed.status ?? null,
          codeDisplay: parsed.code?.coding?.[0]?.display ?? null,
        };
      } catch {
        return null;
      }
    })
    .filter((observation): observation is ObservationDetail => observation !== null);

  const conditions: ConditionDetail[] = resources
    .filter(resource => resource.resourceType === 'Condition')
    .map((resource): ConditionDetail | null => {
      try {
        const parsed = JSON.parse(resource.payload) as {
          subject?: { reference?: string };
          code?: { coding?: Array<{ display?: string }> };
        };
        if (!referenceMatchesPatientId(parsed.subject?.reference, patientId)) {
          return null;
        }
        return {
          codeDisplay: parsed.code?.coding?.[0]?.display ?? null,
          subjectReference: parsed.subject?.reference ?? null,
          resourceType: 'Condition',
        };
      } catch {
        return null;
      }
    })
    .filter((condition): condition is ConditionDetail => condition !== null);

  return { id: patientId, firstName, birthDate, gender, observations, conditions };
}

/** Two distinct phrasings from SmartAuthorizationCodeTokenProvider both mean "no usable token at all, a fresh
 *  interactive sign-in is required." */
export function indicatesReAuthorizationNeeded(message: string): boolean {
  return message.includes('Re-authorize the source') || message.includes('has no authorized token');
}

/** Pulls the signed download link out of a /run response for a workflow whose CSV destination uses Download-URL
 *  delivery — null if no destination node produced one (wrong delivery mode, or zero records written). */
export function extractDownloadUrl(result: WorkflowRunResponse): string | null {
  const destinationOutput = Object.values(result.outputsByNodeId ?? {})
    .find(output => typeof output.metadata?.downloadUrl === 'string');
  return (destinationOutput?.metadata?.downloadUrl as string | undefined) ?? null;
}

@Injectable()
export class PatientStandaloneLaunchService {
  // Resolved once by loadConfig() before any other method here is called (see
  // LaunchStandalonePatientComponent.ngOnInit) — every method below assumes both are already populated.
  private baseUrl = '';
  private workflowId = '';

  constructor(private readonly http: HttpClient) {}

  async loadConfig(): Promise<PatientStandaloneSettingsResponse> {
    const settings = await firstValueFrom(
      this.http.get<PatientStandaloneSettingsResponse>(
        `${HEALTHAPP_BACKEND_BASE_URL}/api/patient-standalone-settings`,
        { withCredentials: true },
      ),
    );
    this.baseUrl = settings.baseUrl;
    this.workflowId = settings.workflowId;
    return settings;
  }

  async loadHospitals(search?: string): Promise<MyChartEndpoint[]> {
    const url = search
      ? `${this.baseUrl}/api/v1/ehr-mychart-endpoints?search=${encodeURIComponent(search)}`
      : `${this.baseUrl}/api/v1/ehr-mychart-endpoints`;
    return firstValueFrom(this.http.get<MyChartEndpoint[]>(url));
  }

  async hasValidToken(workflowId: string, patientId: string | null): Promise<boolean> {
    const status = await firstValueFrom(
      this.http.get<TokenStatusResponse>(
        `${this.baseUrl}/api/v1/workflows/${workflowId}/token-status`,
        { params: patientId ? { patientId } : {} },
      ),
    );
    return status.hasValidToken;
  }

  async run(workflowId: string, patientId: string | null): Promise<WorkflowRunResponse> {
    return firstValueFrom(
      this.http.post<WorkflowRunResponse>(`${this.baseUrl}/api/v1/workflows/${workflowId}/run`, {
        patientId,
        patientSearchCriteria: null,
      }),
    );
  }

  async mintLaunchUrl(workflowId: string, ehrEndpointId: string, callerId?: string): Promise<PublicPatientStandaloneUrlResponse> {
    return firstValueFrom(
      this.http.get<PublicPatientStandaloneUrlResponse>(
        `${this.baseUrl}/api/v1/workflows/${workflowId}/public-patient-standalone-url`,
        { params: callerId ? { ehrEndpointId, callerId } : { ehrEndpointId } },
      ),
    );
  }

  async discardToken(workflowId: string, patientId: string | null): Promise<void> {
    await firstValueFrom(
      this.http.post(
        `${this.baseUrl}/api/v1/workflows/${workflowId}/discard-token`,
        {},
        { params: patientId ? { patientId } : {} },
      ),
    );
  }

  async loadLaunchResultPatientId(workflowRunId: string): Promise<LaunchResultResponse> {
    return firstValueFrom(
      this.http.get<LaunchResultResponse>(
        `${this.baseUrl}/api/v1/workflows/runs/${workflowRunId}/launch-result`,
      ),
    );
  }

  async checkRememberedSession(): Promise<EpicSessionStatusResponse> {
    return firstValueFrom(
      this.http.get<EpicSessionStatusResponse>(
        `${HEALTHAPP_BACKEND_BASE_URL}/api/epic-session/status`,
        { withCredentials: true },
      ),
    );
  }

  async rememberSession(patientId: string | null): Promise<void> {
    await firstValueFrom(
      this.http.post(
        `${HEALTHAPP_BACKEND_BASE_URL}/api/epic-session`,
        { patientId, workflowId: this.workflowId },
        { withCredentials: true },
      ),
    );
  }

  async forgetSession(): Promise<void> {
    await firstValueFrom(
      this.http.delete(`${HEALTHAPP_BACKEND_BASE_URL}/api/epic-session`, { withCredentials: true }),
    );
  }
}
