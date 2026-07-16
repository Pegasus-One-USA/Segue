import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { FHIRBRIDGE_BASE_URL, PATIENT_WORKFLOW_ID } from '../config/standalone-launch.config';
import { environment } from '../../../../../environments/environment';

// Kept only for rememberSession's "which workflow was this launch for" bookkeeping — the token/run/mint/discard
// calls below all take an explicit workflowId parameter instead, since the list fetch and the per-patient detail
// fetch now run against two different workflows (see launch-standalone-patient.ts).

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

// HealthApp's own backend (Demo_TestApp), not FHIRBridge — remembers which patient/workflow this HealthApp user
// last launched, centrally, without requiring any FHIRBridge change. Same mechanism Provider Standalone already
// reuses (EpicSessionStore is keyed purely by HealthApp userId, so a distinct Patient Standalone demo login has its
// own row with no collision).
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

@Injectable()
export class PatientStandaloneLaunchService {
  constructor(private readonly http: HttpClient) {}

  async loadHospitals(search?: string): Promise<MyChartEndpoint[]> {
    const url = search
      ? `${FHIRBRIDGE_BASE_URL}/api/v1/ehr-mychart-endpoints?search=${encodeURIComponent(search)}`
      : `${FHIRBRIDGE_BASE_URL}/api/v1/ehr-mychart-endpoints`;
    return firstValueFrom(this.http.get<MyChartEndpoint[]>(url));
  }

  async hasValidToken(workflowId: string, patientId: string | null): Promise<boolean> {
    const status = await firstValueFrom(
      this.http.get<TokenStatusResponse>(
        `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${workflowId}/token-status`,
        { params: patientId ? { patientId } : {} },
      ),
    );
    return status.hasValidToken;
  }

  async run(workflowId: string, patientId: string | null): Promise<WorkflowRunResponse> {
    return firstValueFrom(
      this.http.post<WorkflowRunResponse>(`${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${workflowId}/run`, {
        patientId,
        patientSearchCriteria: null,
      }),
    );
  }

  async mintLaunchUrl(workflowId: string, ehrEndpointId: string): Promise<PublicPatientStandaloneUrlResponse> {
    return firstValueFrom(
      this.http.get<PublicPatientStandaloneUrlResponse>(
        `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${workflowId}/public-patient-standalone-url`,
        { params: { ehrEndpointId } },
      ),
    );
  }

  async discardToken(workflowId: string, patientId: string | null): Promise<void> {
    await firstValueFrom(
      this.http.post(
        `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${workflowId}/discard-token`,
        {},
        { params: patientId ? { patientId } : {} },
      ),
    );
  }

  async loadLaunchResultPatientId(workflowRunId: string): Promise<LaunchResultResponse> {
    return firstValueFrom(
      this.http.get<LaunchResultResponse>(
        `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/runs/${workflowRunId}/launch-result`,
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
        { patientId, workflowId: PATIENT_WORKFLOW_ID },
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
