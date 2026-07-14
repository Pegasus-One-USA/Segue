import { Component, OnInit, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import { FHIRBRIDGE_BASE_URL, STANDALONE_WORKFLOW_ID } from './core/config/standalone-launch.config';

/** Matches FHIRBridge's PublicEhrEpicEndpointDto (GET /api/v1/ehr-epic-endpoints) — anonymous, EndpointType=Epic
 *  rows only (the vendor's own shared sandbox, never a real customer's MyChart instance). */
interface EpicEndpoint {
  id: string;
  name: string;
  fhirBaseUrl: string;
  status: string;
}

/** Matches OAuthController's BuildLaunchResponse shape (GET /workflows/{id}/public-standalone-url). */
interface PublicStandaloneUrlResponse {
  launchUrl: string;
  mode: 'ehr-launch' | 'standalone' | 'patient';
  opensDirectly: boolean;
  applicationType: string | null;
}

interface LaunchResultResponse {
  patient: unknown;
  patientId: string | null;
}

/** Matches Demo_TestApp/backend's GET /api/epic-session/status. This is HealthApp's own record, not FHIRBridge's —
 *  FHIRBridge never discloses the real Epic token to a third-party app, so this deliberately doesn't hold one
 *  either; it's a "last known good" signal (which patient/workflow, and when a real fetch last confirmed it still
 *  worked), reusable by any third-party app against FHIRBridge's existing, unchanged public API. */
interface EpicSessionStatusResponse {
  hasSession: boolean;
  patientId: string | null;
  workflowId: string | null;
  lastConfirmedValidUtc: string | null;
}

// Matches RankedWorkflowOrchestrator's result shape. A node/token failure that happens mid-run (e.g. Epic rejecting
// a search) is reported via workflowRun.status/errorMessage with a 200 OK — so success can't be judged from "did
// the POST throw" alone. Separately, a failure resolved *before* the run starts (no token cached at all) throws
// past the orchestrator entirely and surfaces as an HTTP 400 with a bare {error: "..."} body instead (see
// Program.cs's global exception handler) — a third, differently-shaped failure mode handled in the catch block.
interface WorkflowRunResponse {
  workflowRun: {
    status: string;
    errorMessage: string | null;
  };
  outputsByNodeId?: Record<string, {
    nodeType: string;
    payload?: { resources?: Array<{ resourceType: string; resourceId: string; payload: string }> };
  }>;
}

interface PatientListEntry {
  id: string;
  name: string;
  birthDate: string | null;
}

/** Pulls the displayable Patient rows out of a /run response's raw EpicSourceNode output — a name search can match
 *  more than one patient, unlike the single-patient flow this screen otherwise deals in. */
function extractPatientList(result: WorkflowRunResponse): PatientListEntry[] {
  const sourceOutput = Object.values(result.outputsByNodeId ?? {}).find(output => output.nodeType === 'EpicSourceNode');
  const resources = sourceOutput?.payload?.resources ?? [];
  return resources
    .filter(resource => resource.resourceType === 'Patient')
    .map((resource): PatientListEntry => {
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

/** Two distinct phrasings from SmartAuthorizationCodeTokenProvider both mean "no usable token at all, a fresh
 *  interactive sign-in is required": an expired token with no way to refresh it, or no token ever cached for this
 *  source+patient. Any other failure (a business-rule error, a transient issue) should leave state intact instead. */
function indicatesReAuthorizationNeeded(message: string): boolean {
  return message.includes('Re-authorize the source') || message.includes('has no authorized token');
}

// HealthApp's own backend (Demo_TestApp), not FHIRBridge — remembers which patient/workflow this HealthApp user
// last launched, centrally (survives across browsers/devices for the same login, unlike the old sessionStorage-only
// approach), without requiring any FHIRBridge change. See EpicSessionStatusResponse.
const HEALTHAPP_BACKEND_BASE_URL = 'http://localhost:5500';

@Component({
  selector: 'app-launch-standalone-provider',
  standalone: true,
  imports: [DatePipe, MatCardModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './launch-standalone-provider.html',
  styleUrl: './launch-standalone-provider.scss',
})
export class LaunchStandaloneProviderComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly hospitals = signal<EpicEndpoint[]>([]);
  readonly isLoadingHospitals = signal(true);
  readonly searchQuery = signal('');
  readonly selectingHospitalId = signal<string | null>(null);
  readonly hospitalSelectError = signal<string | null>(null);
  private searchDebounceHandle: ReturnType<typeof setTimeout> | null = null;

  // Set once FHIRBridge redirects back here after the provider signs into Epic — see ngOnInit. Distinguishes
  // "just landed, pick a hospital" from "token acquired, ready to fetch" without a page-level route change.
  readonly hasEpicToken = signal(false);
  readonly launchError = signal<string | null>(null);

  readonly isFetchingPatientList = signal(false);
  readonly patientListError = signal<string | null>(null);
  readonly patientList = signal<PatientListEntry[] | null>(null);

  // Any raw FHIR search criteria the target server accepts, e.g. "active=true", "identifier=MRN12345",
  // "family=Smith&given=John", "birthdate=1990-01-01", "_count=100" — passed through as-is server-side (see
  // FhirSourceConnectorBase.ApplyPatientScopeAsync), not parsed/validated here. Blank = fall back to whatever
  // patient context (if any) the launch already established.
  readonly patientSearchCriteria = signal('');

  // Non-null once HealthApp's own /api/epic-session confirms a remembered session — the "last known good" timestamp
  // shown on screen. Not a live guarantee; the real authority is always the next actual /run attempt.
  readonly lastConfirmedValidUtc = signal<string | null>(null);

  // True while a remembered session is still being checked (on load) or a just-completed launch's patientId is
  // still being resolved from /launch-result — gates the "Fetch Patient List" button so a click can't race ahead of
  // patientId being set (that race previously made the button silently no-op).
  readonly isResolvingPatientContext = signal(false);

  private patientId: string | null = null;

  constructor(
    private readonly http: HttpClient,
    private readonly route: ActivatedRoute,
  ) {}

  ngOnInit(): void {
    // FHIRBridge's OAuth callback redirects back here with ?workflowRunId=... on success or ?launchError=...
    // if whatever it triggered post-token threw (see OAuthController.Callback) — either way, the token exchange
    // itself is done, so show the "ready to fetch" state instead of the hospital picker again.
    const workflowRunId = this.route.snapshot.queryParamMap.get('workflowRunId');
    const launchError = this.route.snapshot.queryParamMap.get('launchError');
    if (workflowRunId || launchError) {
      // Consume these once, then strip them from the visible URL — a native History API call, not Angular Router
      // navigation. This app doesn't use <router-outlet> for this content (app.html renders it via a plain
      // selectedDemoType() @if/@else-if, not routing), so a router.navigate() here has no component tree to target
      // and risks re-resolving routes/guards this component was never meant to drive. Without stripping the query
      // string at all, though, it stays on the address bar forever — a later Ctrl+F5 (or just revisiting this URL)
      // would re-run this exact branch every time, re-showing "Token Valid" and re-recording HealthApp's session
      // even after Reset Token deliberately cleared it.
      window.history.replaceState(null, '', window.location.pathname);

      this.hasEpicToken.set(true);
      this.launchError.set(launchError);
      // The OAuth token exchange itself has already succeeded by the time either query param appears (see
      // OAuthController.Callback) — that's true regardless of whether the workflow it auto-triggered afterward
      // found a specific patient. Remember the session now, with whatever patientId we have (often none yet, since
      // that auto-triggered run has no search criteria to work with) — a null patientId is valid; FHIRBridge falls
      // back to its unscoped "default" token slot, which this exact login's token was also saved under.
      void this.rememberEpicSession();
      if (workflowRunId) {
        this.isResolvingPatientContext.set(true);
        void this.loadLaunchResultPatientId(workflowRunId);
      }
      return;
    }

    void this.checkRememberedEpicSession();
  }

  // Asks HealthApp's own backend (not FHIRBridge) whether this user has a remembered session — survives across
  // browsers/devices for the same HealthApp login, unlike a purely client-side flag, and needs no FHIRBridge change
  // since it's built entirely from what FHIRBridge's existing API already discloses (a patientId reference, never
  // the real Epic token). If nothing's remembered (or the check itself fails), falls through to the hospital list —
  // the same starting point as before.
  private async checkRememberedEpicSession(): Promise<void> {
    this.isResolvingPatientContext.set(true);
    try {
      const status = await firstValueFrom(
        this.http.get<EpicSessionStatusResponse>(
          `${HEALTHAPP_BACKEND_BASE_URL}/api/epic-session/status`,
          { withCredentials: true },
        ),
      );
      if (status.hasSession) {
        // patientId may legitimately be null here (see ngOnInit's comment) — that's still a remembered, usable
        // session, not an absent one.
        this.patientId = status.patientId;
        this.lastConfirmedValidUtc.set(status.lastConfirmedValidUtc);
        this.hasEpicToken.set(true);
        return;
      }
    } catch {
      // Non-fatal — falls through to the hospital picker below, same as "no remembered session."
    } finally {
      this.isResolvingPatientContext.set(false);
    }

    void this.loadHospitals();
  }

  // Upserts HealthApp's own "last known good" record after a successful launch or fetch — this is what lets
  // checkRememberedEpicSession() skip the hospital picker on a later visit. Best-effort: failing to record this
  // doesn't affect the actual fetch capability, which goes through FHIRBridge regardless. patientId may be null
  // (see ngOnInit's comment) — still worth remembering, since "signed in with no specific patient yet" is a real,
  // usable state (FHIRBridge's unscoped "default" token slot).
  private async rememberEpicSession(): Promise<void> {
    try {
      await firstValueFrom(
        this.http.post(
          `${HEALTHAPP_BACKEND_BASE_URL}/api/epic-session`,
          { patientId: this.patientId, workflowId: STANDALONE_WORKFLOW_ID },
          { withCredentials: true },
        ),
      );
      this.lastConfirmedValidUtc.set(new Date().toISOString());
    } catch {
      // Non-fatal — see comment above.
    }
  }

  private async forgetEpicSession(): Promise<void> {
    try {
      await firstValueFrom(
        this.http.delete(`${HEALTHAPP_BACKEND_BASE_URL}/api/epic-session`, { withCredentials: true }),
      );
    } catch {
      // Non-fatal — worst case, a later visit shows a stale "ready to fetch" that then correctly fails and
      // self-corrects via handleFetchFailure's own forgetEpicSession() call.
    }
  }

  onSearchChange(value: string): void {
    this.searchQuery.set(value);
    if (this.searchDebounceHandle) {
      clearTimeout(this.searchDebounceHandle);
    }
    this.searchDebounceHandle = setTimeout(() => void this.loadHospitals(), 250);
  }

  // Anonymous — no FHIRBridge session exists yet at this point in the flow, so this reads straight from
  // FHIRBridge's public ehr-epic-endpoints listing rather than the Health App backend's own decoupled
  // /api/hospitals (which is unrelated dummy data, not real FHIRBridge configuration).
  private async loadHospitals(): Promise<void> {
    this.isLoadingHospitals.set(true);
    try {
      const query = this.searchQuery().trim();
      const url = query
        ? `${FHIRBRIDGE_BASE_URL}/api/v1/ehr-epic-endpoints?search=${encodeURIComponent(query)}`
        : `${FHIRBRIDGE_BASE_URL}/api/v1/ehr-epic-endpoints`;
      const endpoints = await firstValueFrom(this.http.get<EpicEndpoint[]>(url));
      this.hospitals.set(endpoints);
    } finally {
      this.isLoadingHospitals.set(false);
    }
  }

  private async loadLaunchResultPatientId(workflowRunId: string): Promise<void> {
    try {
      const result = await firstValueFrom(
        this.http.get<LaunchResultResponse>(
          `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/runs/${workflowRunId}/launch-result`,
        ),
      );
      if (result.patientId) {
        this.patientId = result.patientId;
        await this.rememberEpicSession();
      } else {
        this.patientListError.set('FHIRBridge did not return a patient for this launch — check Execution History.');
      }
    } catch {
      this.patientListError.set('Could not read this launch\'s result from FHIRBridge.');
    } finally {
      this.isResolvingPatientContext.set(false);
    }
  }

  // Mints a launch context scoped to THIS hospital via FHIRBridge's anonymous public-standalone-url endpoint
  // (only works if an admin has opted the configured workflow into IsPubliclyLaunchable), then hands the browser
  // off to the resulting Epic authorization URL. Must be a full top-level navigation, not an HttpClient call for
  // the redirect itself: FHIRBridge's endpoint 302s to Epic's real authorization page, which an XHR/fetch can't
  // complete interactively.
  async selectHospital(endpoint: EpicEndpoint): Promise<void> {
    if (this.selectingHospitalId()) {
      return;
    }

    this.selectingHospitalId.set(endpoint.id);
    this.hospitalSelectError.set(null);
    try {
      const result = await firstValueFrom(
        this.http.get<PublicStandaloneUrlResponse>(
          `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${STANDALONE_WORKFLOW_ID}/public-standalone-url`,
          { params: { ehrEndpointId: endpoint.id } },
        ),
      );
      window.location.href = result.launchUrl;
    } catch {
      this.selectingHospitalId.set(null);
      this.hospitalSelectError.set(
        'Could not start sign-in for this hospital. The configured workflow may not be opted into public launch yet.',
      );
    }
  }

  // Triggers the configured workflow directly via FHIRBridge's /run endpoint — no browser redirect, no Epic login
  // screen. Reuses whatever token FHIRBridge already has cached for this source+patient (silently refreshed if
  // it's near expiry). Three distinct failure shapes are possible (see WorkflowRunResponse's comment) — this
  // handles all three, only clearing the remembered patient and dropping back to the hospital picker when the
  // failure genuinely means "no usable token, sign in again"; any other failure just shows the message and leaves
  // state intact so the user can retry the same fetch without redoing the whole OAuth round trip.
  async fetchPatientList(): Promise<void> {
    if (this.isFetchingPatientList() || this.isResolvingPatientContext()) {
      return;
    }

    this.isFetchingPatientList.set(true);
    this.patientListError.set(null);
    try {
      const criteria = this.patientSearchCriteria().trim();
      const result = await firstValueFrom(
        this.http.post<WorkflowRunResponse>(`${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${STANDALONE_WORKFLOW_ID}/run`, {
          patientId: this.patientId,
          patientSearchCriteria: criteria || null,
        }),
      );

      if (result.workflowRun.status === 'Succeeded') {
        this.patientList.set(extractPatientList(result));
        void this.rememberEpicSession();
        return;
      }

      this.handleFetchFailure(result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.');
    } catch (err) {
      // A failure resolved before the run even starts (e.g. no token cached at all) throws past the orchestrator
      // and surfaces here as an HttpErrorResponse with a bare {error: "..."} body, rather than in the 200-OK
      // workflowRun shape above — inspect it the same way rather than assuming every thrown error is a plain
      // connectivity problem.
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      this.handleFetchFailure(
        backendMessage ?? 'Could not reach FHIRBridge to trigger the workflow. Check your connection and try again.',
      );
    } finally {
      this.isFetchingPatientList.set(false);
    }
  }

  private handleFetchFailure(errorMessage: string): void {
    this.patientListError.set(errorMessage);
    if (indicatesReAuthorizationNeeded(errorMessage)) {
      this.resetToken();
    }
  }

  // "Reset Token" — clears HealthApp's own remembered session AND FHIRBridge's actual cached Epic token (so it
  // genuinely can't be reused, not just forgotten locally), then drops back to the hospital picker, forcing a fresh
  // Epic sign-in on the next launch. Same sequence used automatically when a fetch reveals the token needs
  // re-authorization, but callable directly too. Note: this only clears FHIRBridge's own cache — it does not call
  // Epic itself, so if Epic's own browser session (SSO) is still active, the next interactive redirect may not
  // visibly re-prompt for credentials; that's Epic's decision, outside FHIRBridge's or HealthApp's control.
  resetToken(): void {
    void this.forgetEpicSession();
    void this.discardFhirBridgeToken();
    this.patientId = null;
    this.lastConfirmedValidUtc.set(null);
    this.patientList.set(null);
    this.patientListError.set(null);
    this.hasEpicToken.set(false);
    void this.loadHospitals();
  }

  private async discardFhirBridgeToken(): Promise<void> {
    try {
      await firstValueFrom(
        this.http.post(
          `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${STANDALONE_WORKFLOW_ID}/discard-token`,
          {},
          { params: this.patientId ? { patientId: this.patientId } : {} },
        ),
      );
    } catch {
      // Non-fatal — worst case FHIRBridge's cache still has the old token, which the next /run attempt would
      // just successfully reuse (same as if Reset Token had never been clicked); nothing is left in a broken state.
    }
  }
}
