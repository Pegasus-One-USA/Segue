import { Component, NgZone, OnInit, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MatCardModule } from '@angular/material/card';
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

/** Matches WorkflowEndpoints' GET /workflows/{id}/token-status — a cheap, no-pipeline check of whether a real /run
 *  would currently succeed authentication-wise (cache lookup, plus a silent refresh-token call only if the cached
 *  access token has actually expired). Lets the button decide redirect-vs-fetch up front instead of learning it
 *  only from a failed /run attempt. */
interface TokenStatusResponse {
  hasValidToken: boolean;
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

// Matches RankedWorkflowOrchestrator's result shape. A 200 OK here is always a fully Succeeded run — any node/token
// failure (including "no token"/"token expired") throws past the orchestrator before it ever returns, so it never
// surfaces as a 200 with a Failed status; it always lands in the catch block below instead as an HttpErrorResponse
// with a bare {error: "..."} body (see Program.cs's global exception handler). The workflowRun/errorMessage fields
// are kept here defensively in case that ever changes, but the real "did this fail" signal is the catch block.
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

// Carries the in-progress search box value across the full-page redirect to Epic and back, so the auto-fetch that
// follows a fresh sign-in (see ngOnInit) replays the same search the user was trying to run when the token turned
// out to be invalid — rather than silently dropping what they typed.
const PENDING_SEARCH_STORAGE_KEY = 'hb_pending_patient_search';

@Component({
  selector: 'app-launch-standalone-provider',
  standalone: true,
  imports: [DatePipe, MatCardModule, MatProgressSpinnerModule],
  templateUrl: './launch-standalone-provider.html',
  styleUrl: './launch-standalone-provider.scss',
})
export class LaunchStandaloneProviderComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  // Purely informational badge — never gates whether the Fetch button is shown. The real, authoritative check is
  // always the next actual /run attempt (see fetchPatientList); this is just a "last known good" hint carried over
  // from HealthApp's own remembered session or the most recent successful fetch.
  readonly hasEpicToken = signal(false);
  readonly lastConfirmedValidUtc = signal<string | null>(null);

  readonly launchError = signal<string | null>(null);

  readonly isFetchingPatientList = signal(false);
  readonly patientListError = signal<string | null>(null);
  readonly patientList = signal<PatientListEntry[] | null>(null);

  // True while a remembered session is still being checked (on load) or a just-completed launch's patientId is
  // still being resolved from /launch-result — gates the "Fetch Patient List" button so a click can't race ahead of
  // patientId being set.
  readonly isResolvingPatientContext = signal(false);

  // Any raw FHIR search criteria the target server accepts, e.g. "active=true", "identifier=MRN12345",
  // "family=Smith&given=John", "birthdate=1990-01-01", "_count=100" — passed through as-is server-side (see
  // FhirSourceConnectorBase.ApplyPatientScopeAsync), not parsed/validated here. Blank = fall back to whatever
  // patient context (if any) the launch already established.
  readonly patientSearchCriteria = signal('');

  // The hospital list is always visible — step 1 of the flow is picking a hospital, then typing criteria, then
  // clicking Fetch Patient List. Choosing a hospital here only marks it as selected (see chooseHospital); it does
  // NOT redirect immediately. The actual redirect to Epic only happens if a later Fetch Patient List click
  // discovers there's no usable token yet (see handleFetchFailure), using whichever hospital was selected here.
  readonly hospitals = signal<EpicEndpoint[]>([]);
  readonly isLoadingHospitals = signal(false);
  readonly hospitalSearchQuery = signal('');
  readonly selectedHospitalId = signal<string | null>(null);
  readonly hospitalSelectError = signal<string | null>(null);
  // True only while actually redirecting to Epic (a Fetch Patient List click found no usable token and is now
  // minting a launch URL for the selected hospital) — distinct from isFetchingPatientList, which covers the
  // initial "is the token still valid" check.
  readonly isRedirectingToEpic = signal(false);
  private hospitalSearchDebounceHandle: ReturnType<typeof setTimeout> | null = null;

  private patientId: string | null = null;

  constructor(
    private readonly http: HttpClient,
    private readonly ngZone: NgZone,
  ) {}

  ngOnInit(): void {
    // FHIRBridge's OAuth callback redirects back here with one of three markers (see OAuthController.Callback):
    // ?workflowRunId=... when its own no-criteria convenience run got a real run id, ?launchError=... when that run
    // was attempted but threw, or ?signedIn=1 when the convenience run was deliberately skipped altogether (a
    // Standalone/patient-standalone sign-in with no upfront launch context — see
    // InteractiveSourceAuthorizationService.CompleteAsync's skipWorkflowTrigger, which is the common case for this
    // component). All three only ever appear once the token exchange itself has already succeeded. Read straight
    // off window.location rather than ActivatedRoute: this app never uses a real <router-outlet> for this content
    // (app.html renders it via a plain selectedDemoType() @if/@else-if), so ActivatedRoute here is only ever
    // reflecting the root route's state, which depends on Angular Router's own async initialization having
    // settled — a race that, on the exact page load right after this full-page redirect back from Epic, could read
    // back an empty query param map before the Router catches up, silently skipping the auto-fetch branch below
    // with no error at all. window.location.search has no such dependency.
    const params = new URLSearchParams(window.location.search);
    const workflowRunId = params.get('workflowRunId');
    const launchError = params.get('launchError');
    const signedIn = params.get('signedIn');
    if (workflowRunId || launchError || signedIn) {
      // Consume these once, then strip them from the visible URL — a native History API call, not Angular Router
      // navigation. This app doesn't use <router-outlet> for this content (app.html renders it via a plain
      // selectedDemoType() @if/@else-if, not routing), so a router.navigate() here has no component tree to target
      // and risks re-resolving routes/guards this component was never meant to drive. Without stripping the query
      // string at all, though, it stays on the address bar forever — a later Ctrl+F5 (or just revisiting this URL)
      // would re-run this exact branch every time, re-triggering an auto-fetch even after Reset Token deliberately
      // cleared the session.
      window.history.replaceState(null, '', window.location.pathname);
      void this.loadHospitals();

      this.hasEpicToken.set(true);
      this.launchError.set(launchError);

      // Restore whatever the user had typed into the search box before the redirect fired (see redirectToEpic),
      // so the auto-fetch below replays it instead of running an unscoped search.
      const pendingCriteria = sessionStorage.getItem(PENDING_SEARCH_STORAGE_KEY);
      sessionStorage.removeItem(PENDING_SEARCH_STORAGE_KEY);
      if (pendingCriteria) {
        this.patientSearchCriteria.set(pendingCriteria);
      }

      // The OAuth token exchange itself has already succeeded by the time either query param appears (see
      // OAuthController.Callback) — that's true regardless of whether the workflow it auto-triggered afterward
      // found a specific patient. Remember the session now, with whatever patientId we have (often none yet, since
      // that auto-triggered run has no search criteria to work with) — a null patientId is valid; FHIRBridge falls
      // back to its unscoped "default" token slot, which this exact login's token was also saved under.
      void this.rememberEpicSession();

      // Fire the real, criteria-scoped fetch now that the token is in place — this is the "get grant access and
      // token and fetch the data" leg completing in one flow, rather than requiring a second manual click.
      //
      // workflowRunId only ever points at FHIRBridge's OWN no-criteria convenience run, auto-triggered by
      // OAuthController.Callback purely to warm the token cache — it has no visibility into anything typed in this
      // not-yet-loaded page. For a Standalone launch with no upfront patient context, that convenience run ALWAYS
      // fails Epic's "requires demographics or _id" business rule, and per RankedWorkflowOrchestrator re-throwing
      // on every node failure, TriggerWorkflowRunAsync's catch then ALWAYS discards the real run id — so
      // OAuthController falls through to launchError=workflow_failed instead of workflowRunId for this extremely
      // common case. Gating the auto-fetch on `if (workflowRunId)` alone meant it silently never ran for this path;
      // the fetch below must not depend on which query param came back, only on the token exchange having
      // succeeded (true for either one — see comment above).
      if (workflowRunId) {
        this.isResolvingPatientContext.set(true);
        void this.loadLaunchResultPatientId(workflowRunId).then(succeeded => {
          // Explicitly re-entering NgZone here is load-bearing, not defensive: WorkflowRun history showed this
          // auto-fetch's /run call actually succeeding server-side (patientList did get the real data) several
          // seconds before anything appeared on screen — nothing repainted until the user's next click forced a
          // change-detection pass. Two chained promise hops deep from ngOnInit (this .then(), then fetchPatientList's
          // own await), the continuation was landing outside Angular's zone, so the signal write was real but no CD
          // tick ever followed it.
          if (succeeded) {
            void this.ngZone.run(() => this.fetchPatientList(pendingCriteria ?? undefined));
          }
        });
      } else {
        // launchError=workflow_failed (or any other shape without a workflowRunId): the token itself is still
        // good, there's just nothing further to resolve from the convenience run — go straight to the real fetch.
        void this.ngZone.run(() => this.fetchPatientList(pendingCriteria ?? undefined));
      }
      return;
    }

    // The hospital list is always visible from the start (step 1 of the flow), regardless of whether a remembered
    // session exists — the user may still want to pick/change the target hospital before clicking Fetch.
    void this.loadHospitals();
    void this.checkRememberedEpicSession();
  }

  // Asks HealthApp's own backend (not FHIRBridge) whether this user has a remembered session — survives across
  // browsers/devices for the same HealthApp login, unlike a purely client-side flag. Purely hydrates the informational
  // "Token Valid" badge and a patientId hint; does not gate anything, since the button always runs the same
  // check-then-fetch-or-redirect logic regardless of what this finds.
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
      }
    } catch {
      // Non-fatal — the badge just stays "not signed in yet"; the button's own check-then-fetch-or-redirect logic
      // is unaffected either way.
    } finally {
      this.isResolvingPatientContext.set(false);
    }
  }

  // Upserts HealthApp's own "last known good" record after a successful launch or fetch — this is what lets
  // checkRememberedEpicSession() show "Token Valid" on a later visit. Best-effort: failing to record this doesn't
  // affect the actual fetch capability, which goes through FHIRBridge regardless. patientId may be null (see
  // ngOnInit's comment) — still worth remembering, since "signed in with no specific patient yet" is a real, usable
  // state (FHIRBridge's unscoped "default" token slot).
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
      // Non-fatal — worst case, a later visit shows a stale "Token Valid" that then correctly fails and
      // self-corrects via handleFetchFailure's own forgetEpicSession() call.
    }
  }

  private async loadLaunchResultPatientId(workflowRunId: string): Promise<boolean> {
    try {
      const result = await firstValueFrom(
        this.http.get<LaunchResultResponse>(
          `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/runs/${workflowRunId}/launch-result`,
        ),
      );
      if (result.patientId) {
        this.patientId = result.patientId;
        await this.rememberEpicSession();
        return true;
      }

      this.patientListError.set('FHIRBridge did not return a patient for this launch — check Execution History.');
      return true; // No specific patient, but the token itself is still good — still worth auto-fetching.
    } catch {
      this.patientListError.set('Could not read this launch\'s result from FHIRBridge.');
      return false;
    } finally {
      this.isResolvingPatientContext.set(false);
    }
  }

  // The single entry point for the button. Checks token-status first (a cheap, no-pipeline lookup — see
  // TokenStatusResponse) so an already-known-invalid token redirects straight to Epic without wasting a /run call;
  // only when that check says a token IS valid (or itself fails to answer, e.g. a network hiccup) does this go on
  // to attempt the real fetch. The real fetch's own error handling (see handleFetchFailure) stays as a safety net
  // for the rarer case where the token status check said valid but Epic disagrees anyway (e.g. an out-of-band
  // revocation) — so this never becomes stricter than a direct /run attempt, only faster in the common case.
  // criteriaOverride lets the post-redirect auto-fetch (see ngOnInit) pass the just-restored pending criteria
  // directly, rather than relying on the patientSearchCriteria signal having already propagated across the async
  // boundary by the time this reads it.
  async fetchPatientList(criteriaOverride?: string): Promise<void> {
    if (this.isFetchingPatientList() || this.isResolvingPatientContext() || this.isRedirectingToEpic()) {
      return;
    }

    this.isFetchingPatientList.set(true);
    this.patientListError.set(null);
    try {
      if (!(await this.hasValidToken())) {
        await this.needsReAuthorization();
        return;
      }

      const criteria = (criteriaOverride ?? this.patientSearchCriteria()).trim();
      const result = await firstValueFrom(
        this.http.post<WorkflowRunResponse>(`${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${STANDALONE_WORKFLOW_ID}/run`, {
          patientId: this.patientId,
          patientSearchCriteria: criteria || null,
        }),
      );

      if (result.workflowRun.status === 'Succeeded') {
        this.patientList.set(extractPatientList(result));
        this.hasEpicToken.set(true);
        // Clears any stale "workflow_failed" banner from FHIRBridge's own no-criteria convenience run (see
        // ngOnInit) — that run failing is an expected, benign artifact of the Standalone launch flow, not a real
        // problem, and showing it alongside a successful, criteria-scoped patient list would be misleading.
        this.launchError.set(null);
        void this.rememberEpicSession();
        return;
      }

      await this.handleFetchFailure(result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.');
    } catch (err) {
      // A failure resolved before the run even starts (e.g. no token cached at all, or an expired one) throws past
      // the orchestrator and surfaces here as an HttpErrorResponse with a bare {error: "..."} body — this is the
      // normal, expected shape for "you need to sign in again", not just a plain connectivity problem.
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      await this.handleFetchFailure(
        backendMessage ?? 'Could not reach FHIRBridge to trigger the workflow. Check your connection and try again.',
      );
    } finally {
      this.isFetchingPatientList.set(false);
    }
  }

  // Cheap pre-check: does FHIRBridge currently have (or can it silently refresh) a usable token, without running
  // any pipeline? Defaults to "assume valid" on any error (network hiccup, endpoint unavailable) so a broken check
  // never blocks the flow — the real /run call right after is always the authoritative test either way.
  private async hasValidToken(): Promise<boolean> {
    try {
      const status = await firstValueFrom(
        this.http.get<TokenStatusResponse>(
          `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${STANDALONE_WORKFLOW_ID}/token-status`,
          { params: this.patientId ? { patientId: this.patientId } : {} },
        ),
      );
      return status.hasValidToken;
    } catch {
      return true;
    }
  }

  // Interprets the fetch failure: if it genuinely means "no usable token", delegates to needsReAuthorization().
  // Any other failure (a business-rule error, a transient issue) just shows the message and leaves state intact so
  // the user can retry the same fetch without redoing the whole OAuth round trip.
  private async handleFetchFailure(errorMessage: string): Promise<void> {
    if (!indicatesReAuthorizationNeeded(errorMessage)) {
      this.patientListError.set(errorMessage);
      return;
    }

    await this.needsReAuthorization();
  }

  // Clears the stale local/FHIRBridge state and redirects to Epic using whichever hospital the user already
  // selected above — the "if not [valid], goto epic, grant access, fetch data and display" leg. Reached either
  // directly from a negative token-status check (the common, fast path) or from handleFetchFailure as a fallback
  // (the token-status check said valid, or errored and defaulted to "assume valid", but the real fetch disagreed
  // anyway). If the user hasn't selected a hospital yet, there's nothing to redirect to, so this just asks them to
  // pick one instead of guessing.
  private async needsReAuthorization(): Promise<void> {
    void this.forgetEpicSession();
    this.patientId = null;
    this.hasEpicToken.set(false);
    this.lastConfirmedValidUtc.set(null);

    const selectedHospital = this.hospitals().find(hospital => hospital.id === this.selectedHospitalId());
    if (!selectedHospital) {
      this.patientListError.set('Select a hospital above, then click Fetch Patient List again to sign in.');
      return;
    }

    await this.redirectToEpic(selectedHospital);
  }

  onHospitalSearchChange(value: string): void {
    this.hospitalSearchQuery.set(value);
    if (this.hospitalSearchDebounceHandle) {
      clearTimeout(this.hospitalSearchDebounceHandle);
    }
    this.hospitalSearchDebounceHandle = setTimeout(() => void this.loadHospitals(), 250);
  }

  // Anonymous — no FHIRBridge session exists yet at this point, so this reads straight from FHIRBridge's public
  // ehr-epic-endpoints listing.
  private async loadHospitals(): Promise<void> {
    this.isLoadingHospitals.set(true);
    try {
      const query = this.hospitalSearchQuery().trim();
      const url = query
        ? `${FHIRBRIDGE_BASE_URL}/api/v1/ehr-epic-endpoints?search=${encodeURIComponent(query)}`
        : `${FHIRBRIDGE_BASE_URL}/api/v1/ehr-epic-endpoints`;
      const endpoints = await firstValueFrom(this.http.get<EpicEndpoint[]>(url));
      this.hospitals.set(endpoints);
    } catch {
      this.hospitalSelectError.set('Could not load the hospital list from FHIRBridge.');
    } finally {
      this.isLoadingHospitals.set(false);
    }
  }

  // Step 1 of the flow: just marks which hospital the user intends to use — no network call, no redirect. The user
  // can still type search criteria (step 2) after this before clicking Fetch Patient List (step 3), which is the
  // only thing that actually acts on this selection (see handleFetchFailure/redirectToEpic).
  chooseHospital(endpoint: EpicEndpoint): void {
    this.selectedHospitalId.set(endpoint.id);
    this.hospitalSelectError.set(null);
  }

  // Mints a launch context for the pre-selected hospital via FHIRBridge's anonymous public-standalone-url endpoint,
  // stashes the in-progress search box value so it can be replayed once we're back (see ngOnInit), and hands the
  // browser off to Epic's real authorization page. Must be a full top-level navigation, not an HttpClient call:
  // FHIRBridge's endpoint 302s to Epic, which an XHR/fetch can't complete interactively. Only reached from
  // handleFetchFailure, once a Fetch Patient List click has confirmed there's no usable token.
  private async redirectToEpic(endpoint: EpicEndpoint): Promise<void> {
    this.isRedirectingToEpic.set(true);
    this.hospitalSelectError.set(null);
    try {
      const criteria = this.patientSearchCriteria().trim();
      if (criteria) {
        sessionStorage.setItem(PENDING_SEARCH_STORAGE_KEY, criteria);
      } else {
        sessionStorage.removeItem(PENDING_SEARCH_STORAGE_KEY);
      }

      const result = await firstValueFrom(
        this.http.get<PublicStandaloneUrlResponse>(
          `${FHIRBRIDGE_BASE_URL}/api/v1/workflows/${STANDALONE_WORKFLOW_ID}/public-standalone-url`,
          { params: { ehrEndpointId: endpoint.id } },
        ),
      );
      window.location.href = result.launchUrl;
    } catch {
      this.isRedirectingToEpic.set(false);
      this.hospitalSelectError.set(
        'Could not start sign-in for this hospital. The configured workflow may not be opted into public launch yet.',
      );
    }
  }

  // "Reset Token" — clears HealthApp's own remembered session AND FHIRBridge's actual cached Epic token (so it
  // genuinely can't be reused, not just forgotten locally). The next "Fetch Patient List" click will then correctly
  // fail its check and redirect to Epic for a fresh sign-in, same as the automatic path in handleFetchFailure. Note:
  // this only clears FHIRBridge's own cache — it does not call Epic itself, so if Epic's own browser session (SSO)
  // is still active, the next interactive redirect may not visibly re-prompt for credentials; that's Epic's
  // decision, outside FHIRBridge's or HealthApp's control.
  resetToken(): void {
    void this.forgetEpicSession();
    void this.discardFhirBridgeToken();
    this.patientId = null;
    this.lastConfirmedValidUtc.set(null);
    this.patientList.set(null);
    this.patientListError.set(null);
    this.hasEpicToken.set(false);
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
