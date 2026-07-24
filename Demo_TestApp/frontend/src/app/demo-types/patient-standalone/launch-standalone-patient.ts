import { Component, NgZone, OnInit, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  FetchedPatient,
  MyChartEndpoint,
  PatientDetail,
  PatientStandaloneLaunchService,
  extractDownloadUrl,
  extractFetchedPatients,
  extractPatientDetail,
  indicatesReAuthorizationNeeded,
} from './core/services/patient-standalone-launch.service';
import {
  CSV_EMAIL_EXPORT_WORKFLOW_ID,
  CSV_EXPORT_WORKFLOW_ID,
} from './core/config/standalone-launch.config';

@Component({
  selector: 'app-launch-standalone-patient',
  standalone: true,
  imports: [DatePipe, MatProgressSpinnerModule],
  providers: [PatientStandaloneLaunchService],
  templateUrl: './launch-standalone-patient.html',
  styleUrl: './launch-standalone-patient.scss',
})
export class LaunchStandalonePatientComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  // Deliberately a fixed redirect to the app root, not window.history.back(): a real MyChart sign-in round trip
  // pushes several history entries (dashboard → this page → the external MyChart login page → back here), so one
  // history.back() would land on the external MyChart page rather than the demo-type-1 dashboard this component is
  // always reached from. The root path re-renders that dashboard directly (the Patient role persists in
  // sessionStorage — see app.ts's AUTH_ROLE_STORAGE_KEY — and app.html's isOnPatientStandaloneLaunchPath check is
  // false there).
  goHome(): void {
    window.location.href = '/';
  }

  // Purely informational badge — never gates whether the Connect button is shown. The real, authoritative check is
  // always the next actual /run attempt (see fetchPatient); this is just a "last known good" hint carried over from
  // HealthApp's own remembered session or the most recent successful fetch.
  readonly hasMyChartToken = signal(false);
  readonly lastConfirmedValidUtc = signal<string | null>(null);

  readonly launchError = signal<string | null>(null);

  // Set when loadConfig() comes back with no PatientWorkflowId configured yet (WorkflowSettingsEntity's default is
  // empty — see HealthAppDbContext) — every other call in this component assumes a real workflow id, so this short-
  // circuits ngOnInit before any of them fire instead of letting them all 404 individually.
  readonly configError = signal<string | null>(null);

  readonly isFetchingPatient = signal(false);
  readonly patientError = signal<string | null>(null);
  readonly fetchedPatients = signal<FetchedPatient[] | null>(null);

  // Detail section (Patient/Observation/Condition) for whichever row was last clicked in fetchedPatients — a
  // second, patient-scoped /run triggered on click, reusing the same already-valid token from the list fetch
  // rather than a fresh sign-in. Cleared whenever a new list fetch replaces fetchedPatients.
  readonly selectedPatientId = signal<string | null>(null);
  readonly isFetchingPatientDetail = signal(false);
  readonly patientDetailError = signal<string | null>(null);
  readonly patientDetail = signal<PatientDetail | null>(null);

  // "Download Patient Information" — a third, independent workflow (CSV_EXPORT_WORKFLOW_ID) whose destination uses
  // Download-URL delivery. Only enabled once a specific patient's detail is open, since it downloads for whichever
  // patientId is currently selected (see downloadPatientInformation).
  readonly isDownloadingPatientInfo = signal(false);
  readonly downloadError = signal<string | null>(null);

  // "Email Patient Information" — same shape as the download button above, but backed by CSV_EMAIL_EXPORT_WORKFLOW_ID
  // (Email delivery instead of Download-URL). A Succeeded run means the file was already sent server-side, so there's
  // nothing to navigate to — emailSuccess just confirms it happened.
  readonly isEmailingPatientInfo = signal(false);
  readonly emailError = signal<string | null>(null);
  readonly emailSuccess = signal(false);

  // True while a remembered session is still being checked (on load) or a just-completed launch's patientId is
  // still being resolved from /launch-result — gates the Connect button so a click can't race ahead of patientId
  // being set.
  readonly isResolvingPatientContext = signal(false);

  // The hospital list is always visible — step 1 of the flow is picking a hospital, then clicking Connect. Choosing
  // a hospital only marks it as selected (see chooseHospital); it does NOT redirect immediately. The actual
  // redirect to MyChart only happens if a later Connect click discovers there's no usable token yet.
  readonly hospitals = signal<MyChartEndpoint[]>([]);
  readonly isLoadingHospitals = signal(false);
  readonly hospitalSearchQuery = signal('');
  readonly selectedHospitalId = signal<string | null>(null);
  readonly hospitalSelectError = signal<string | null>(null);
  // True only while actually redirecting to MyChart — distinct from isFetchingPatient, which covers the initial
  // "is the token still valid" check.
  readonly isRedirectingToMyChart = signal(false);
  private hospitalSearchDebounceHandle: ReturnType<typeof setTimeout> | null = null;

  private patientId: string | null = null;

  // Resolved once by initialize()'s loadConfig() call before anything below runs — see WorkflowSettingsEntity for
  // where these now live (formerly a gitignored per-developer local file, standalone-launch.config.ts). Two
  // distinct workflow ids: workflowId drives the list fetch, detailWorkflowId the separate per-patient detail fetch
  // triggered by viewPatientDetail — each has its own independent FHIRBridge public-launch opt-in.
  private workflowId = '';
  private detailWorkflowId = '';

  constructor(
    private readonly launchService: PatientStandaloneLaunchService,
    private readonly ngZone: NgZone,
  ) {}

  ngOnInit(): void {
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    // FHIRBridge's OAuth callback redirects back here with one of three markers (see OAuthController.Callback):
    // ?workflowRunId=... when its own no-criteria convenience run got a real run id, ?launchError=... when that run
    // was attempted but threw, or ?signedIn=1 when the convenience run was deliberately skipped altogether (the
    // common case for a Patient Standalone sign-in with no upfront search criteria). All three only ever appear
    // once the token exchange itself has already succeeded. Read straight off window.location rather than
    // ActivatedRoute: this app never uses a real <router-outlet> for this content (app.html renders it via a plain
    // role() @if/@else-if), so ActivatedRoute here could race Angular Router's own async
    // initialization on the exact page load right after this full-page redirect back from MyChart. This read is
    // synchronous and needs no config, so it happens before loadConfig() even starts.
    const params = new URLSearchParams(window.location.search);
    const workflowRunId = params.get('workflowRunId');
    const launchError = params.get('launchError');
    const signedIn = params.get('signedIn');
    const isOAuthCallback = !!(workflowRunId || launchError || signedIn);

    // checkRememberedSession() hits HealthApp's own backend and has no dependency on the FHIRBridge workflow
    // id/base URL loadConfig() resolves below — kick it off in parallel rather than serializing it behind a
    // request it doesn't need. Skipped entirely on the OAuth-callback path, matching the original behavior (that
    // branch calls rememberSession() itself once the token exchange is confirmed, not this "was there already a
    // remembered session" check).
    const rememberedSessionCheck = isOAuthCallback ? null : this.checkRememberedSession();

    // Resolve the admin-configured workflow id + base URL before anything else that needs them runs — every other
    // call this component makes (loadHospitals, hasValidToken, run, ...) goes through
    // PatientStandaloneLaunchService's own baseUrl/workflowId fields, which loadConfig() is what populates. See
    // WorkflowSettingsEntity for where these now live (formerly a gitignored per-developer local file).
    let settings;
    try {
      settings = await this.launchService.loadConfig();
    } catch {
      this.configError.set('Could not load Patient Standalone configuration from HealthApp. Check your connection and try again.');
      return;
    }

    if (!settings.workflowId || !settings.baseUrl) {
      this.configError.set(
        'Patient Standalone is not configured yet. Ask an admin to set the Workflow Id and Base URL in Workflow Settings.',
      );
      return;
    }

    this.workflowId = settings.workflowId;
    this.detailWorkflowId = settings.detailWorkflowId;

    if (isOAuthCallback) {
      await this.handleOAuthCallback(workflowRunId, launchError);
      return;
    }

    // The hospital list is always visible from the start (step 1 of the flow), regardless of whether a remembered
    // session exists — the user may still want to pick/change the target hospital before clicking Connect.
    void this.loadHospitals();
    void rememberedSessionCheck;
  }

  // The "just redirected back from MyChart" leg of initialize() — consumes the OAuth callback's query params once,
  // then either resolves the auto-triggered convenience run's patient before fetching, or fetches directly.
  // Extracted out of initialize() so that method reads as "resolve config, then pick a branch" rather than mixing
  // config-resolution with this branch's own multi-step logic.
  private async handleOAuthCallback(workflowRunId: string | null, launchError: string | null): Promise<void> {
    // Consume these once, then strip them from the visible URL via the native History API — this app doesn't use
    // <router-outlet> for this content, so a router.navigate() here has no component tree to target. Without
    // stripping the query string, a later Ctrl+F5 (or just revisiting this URL) would re-run this exact branch
    // every time, re-triggering an auto-fetch even after Reset Token deliberately cleared the session.
    window.history.replaceState(null, '', window.location.pathname);
    void this.loadHospitals();

    this.hasMyChartToken.set(true);
    this.launchError.set(launchError);

    // The OAuth token exchange itself has already succeeded by the time either query param appears — that's true
    // regardless of whether the workflow it auto-triggered afterward found a specific patient. Remember the
    // session now, with whatever patientId we have (often none yet) — a null patientId is valid; FHIRBridge falls
    // back to its unscoped "default" token slot, which this exact login's token was also saved under.
    void this.rememberSession();

    // Fire the real fetch now that the token is in place — this is the "get grant access and token and fetch the
    // data" leg completing in one flow, rather than requiring a second manual click. Gating only on
    // `if (workflowRunId)` would silently skip this for the extremely common "convenience run skipped, signedIn=1
    // only" case (see InteractiveSourceAuthorizationService.CompleteAsync's skipWorkflowTrigger) — the fetch below
    // must not depend on which query param came back, only on the token exchange having succeeded (true for
    // either one).
    if (workflowRunId) {
      this.isResolvingPatientContext.set(true);
      void this.loadLaunchResultPatientId(workflowRunId).then(succeeded => {
        // Explicitly re-entering NgZone here is load-bearing, not defensive — two chained promise hops deep from
        // ngOnInit (this .then(), then fetchPatient's own await), the continuation can land outside Angular's
        // zone, so a signal write happens but no change-detection tick ever follows it.
        if (succeeded) {
          void this.ngZone.run(() => this.fetchPatient());
        }
      });
    } else {
      void this.ngZone.run(() => this.fetchPatient());
    }
  }

  // Asks HealthApp's own backend (not FHIRBridge) whether this user has a remembered session — survives across
  // browsers/devices for the same HealthApp login. Purely hydrates the informational "Token Valid" badge and a
  // patientId hint; does not gate anything, since the button always runs the same check-then-fetch-or-redirect
  // logic regardless of what this finds.
  private async checkRememberedSession(): Promise<void> {
    this.isResolvingPatientContext.set(true);
    try {
      const status = await this.launchService.checkRememberedSession();
      if (status.hasSession) {
        this.patientId = status.patientId;
        this.lastConfirmedValidUtc.set(status.lastConfirmedValidUtc);
        this.hasMyChartToken.set(true);
      }
    } catch {
      // Non-fatal — the badge just stays "not signed in yet"; the button's own check-then-fetch-or-redirect logic
      // is unaffected either way.
    } finally {
      this.isResolvingPatientContext.set(false);
    }
  }

  // Upserts HealthApp's own "last known good" record after a successful launch or fetch.
  private async rememberSession(): Promise<void> {
    try {
      await this.launchService.rememberSession(this.patientId);
      this.lastConfirmedValidUtc.set(new Date().toISOString());
    } catch {
      // Non-fatal — see comment above.
    }
  }

  private async forgetSession(): Promise<void> {
    try {
      await this.launchService.forgetSession();
    } catch {
      // Non-fatal — worst case, a later visit shows a stale "Token Valid" that then correctly fails and
      // self-corrects via handleFetchFailure's own forgetSession() call.
    }
  }

  private async loadLaunchResultPatientId(workflowRunId: string): Promise<boolean> {
    try {
      const result = await this.launchService.loadLaunchResultPatientId(workflowRunId);
      if (result.patientId) {
        this.patientId = result.patientId;
        await this.rememberSession();
        return true;
      }

      this.patientError.set('FHIRBridge did not return a patient for this launch — check Execution History.');
      return true; // No specific patient, but the token itself is still good — still worth auto-fetching.
    } catch {
      this.patientError.set('Could not read this launch\'s result from FHIRBridge.');
      return false;
    } finally {
      this.isResolvingPatientContext.set(false);
    }
  }

  // The single entry point for the Connect button. Checks token-status first (a cheap, no-pipeline lookup) so an
  // already-known-invalid token redirects straight to MyChart without wasting a /run call; only when that check
  // says a token IS valid (or itself fails to answer) does this go on to attempt the real fetch. This is the same
  // button clicked again to prove the "already have a valid token" branch: with a valid token, clicking Connect
  // fetches immediately with no redirect at all.
  async fetchPatient(): Promise<void> {
    if (this.isFetchingPatient() || this.isResolvingPatientContext() || this.isRedirectingToMyChart()) {
      return;
    }

    this.isFetchingPatient.set(true);
    this.patientError.set(null);
    try {
      if (!(await this.hasValidToken(this.workflowId))) {
        await this.needsReAuthorization(this.workflowId);
        return;
      }

      const result = await this.launchService.run(this.workflowId, this.patientId);

      if (result.workflowRun.status === 'Succeeded') {
        this.fetchedPatients.set(extractFetchedPatients(result));
        this.hasMyChartToken.set(true);
        // Clears any stale "workflow_failed" banner from FHIRBridge's own no-criteria convenience run — that run
        // failing is an expected, benign artifact of the Standalone launch flow, not a real problem.
        this.launchError.set(null);
        // A fresh list invalidates whatever detail section was open for a row from the previous list.
        this.selectedPatientId.set(null);
        this.patientDetail.set(null);
        this.patientDetailError.set(null);
        void this.rememberSession();
        return;
      }

      await this.handleFetchFailure(
        result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.', this.workflowId,
      );
    } catch (err) {
      // A failure resolved before the run even starts (e.g. no token cached at all, or an expired one) throws past
      // the orchestrator and surfaces here as an HttpErrorResponse with a bare {error: "..."} body.
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      await this.handleFetchFailure(
        backendMessage ?? 'Could not reach FHIRBridge to trigger the workflow. Check your connection and try again.',
        this.workflowId,
      );
    } finally {
      this.isFetchingPatient.set(false);
    }
  }

  // Clicking a patient row triggers a second, patient-scoped /run against the detail workflow — a deliberately
  // different workflow from the one that produced the list, each with its own public-launch opt-in (see
  // detailWorkflowId/workflowId, both resolved once by initialize()'s loadConfig()). Still reuses whatever token
  // that workflow's own source connection already has cached, not
  // an unconditional fresh sign-in — same token-status-then-run shape as fetchPatient() above, so an out-of-band
  // token revocation (or this workflow simply never having been signed into yet) is handled the same way (redirect
  // to MyChart) rather than silently failing.
  async viewPatientDetail(patient: FetchedPatient): Promise<void> {
    if (this.isFetchingPatientDetail() || this.isRedirectingToMyChart()) {
      return;
    }

    this.selectedPatientId.set(patient.id);
    this.isFetchingPatientDetail.set(true);
    this.patientDetailError.set(null);
    this.patientDetail.set(null);
    try {
      if (!(await this.hasValidToken(this.detailWorkflowId))) {
        await this.needsReAuthorization(this.detailWorkflowId);
        return;
      }

      const result = await this.launchService.run(this.detailWorkflowId, patient.id);

      if (result.workflowRun.status === 'Succeeded') {
        const detail = extractPatientDetail(result, patient.id);
        this.patientDetail.set(detail);
        if (!detail) {
          this.patientDetailError.set('FHIRBridge did not return any resources for this patient.');
        }
        return;
      }

      const errorMessage = result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.';
      if (indicatesReAuthorizationNeeded(errorMessage)) {
        await this.needsReAuthorization(this.detailWorkflowId);
        return;
      }
      this.patientDetailError.set(errorMessage);
    } catch (err) {
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      const errorMessage = backendMessage
        ?? 'Could not reach FHIRBridge to fetch this patient\'s detail. Check your connection and try again.';
      if (indicatesReAuthorizationNeeded(errorMessage)) {
        await this.needsReAuthorization(this.detailWorkflowId);
        return;
      }
      this.patientDetailError.set(errorMessage);
    } finally {
      this.isFetchingPatientDetail.set(false);
    }
  }

  closePatientDetail(): void {
    this.selectedPatientId.set(null);
    this.patientDetail.set(null);
    this.patientDetailError.set(null);
    this.downloadError.set(null);
    this.emailError.set(null);
    this.emailSuccess.set(false);
  }

  // Triggers CSV_EXPORT_WORKFLOW_ID for whichever patient's detail is currently open. That workflow's destination
  // uses Download-URL delivery, so a Succeeded run returns a signed link (extractDownloadUrl) rather than resource
  // JSON — navigating the browser to it is enough to download, since the response carries
  // Content-Disposition: attachment (no synthetic anchor/blob handling needed). Same token-status-then-run shape as
  // fetchPatient/viewPatientDetail: an already-invalid token redirects to MyChart rather than wasting a /run call.
  async downloadPatientInformation(): Promise<void> {
    const patientId = this.selectedPatientId();
    if (!patientId || this.isDownloadingPatientInfo() || this.isRedirectingToMyChart()) {
      return;
    }

    this.isDownloadingPatientInfo.set(true);
    this.downloadError.set(null);
    try {
      if (!(await this.hasValidToken(CSV_EXPORT_WORKFLOW_ID))) {
        await this.needsReAuthorization(CSV_EXPORT_WORKFLOW_ID);
        return;
      }

      const result = await this.launchService.run(CSV_EXPORT_WORKFLOW_ID, patientId);

      if (result.workflowRun.status !== 'Succeeded') {
        const errorMessage = result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.';
        if (indicatesReAuthorizationNeeded(errorMessage)) {
          await this.needsReAuthorization(CSV_EXPORT_WORKFLOW_ID);
          return;
        }
        this.downloadError.set(errorMessage);
        return;
      }

      const downloadUrl = extractDownloadUrl(result);
      if (!downloadUrl) {
        this.downloadError.set('FHIRBridge did not return a download link for this export.');
        return;
      }

      window.location.href = downloadUrl;
    } catch (err) {
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      const errorMessage = backendMessage
        ?? 'Could not reach FHIRBridge to generate this download. Check your connection and try again.';
      if (indicatesReAuthorizationNeeded(errorMessage)) {
        await this.needsReAuthorization(CSV_EXPORT_WORKFLOW_ID);
        return;
      }
      this.downloadError.set(errorMessage);
    } finally {
      this.isDownloadingPatientInfo.set(false);
    }
  }

  // Triggers CSV_EMAIL_EXPORT_WORKFLOW_ID for whichever patient's detail is currently open. That workflow's
  // destination uses Email delivery, so a Succeeded run has already sent the file server-side — there is no link or
  // bytes to hand back, just a status. Same token-status-then-run shape as downloadPatientInformation.
  async emailPatientInformation(): Promise<void> {
    const patientId = this.selectedPatientId();
    if (!patientId || this.isEmailingPatientInfo() || this.isRedirectingToMyChart()) {
      return;
    }

    this.isEmailingPatientInfo.set(true);
    this.emailError.set(null);
    this.emailSuccess.set(false);
    try {
      if (!(await this.hasValidToken(CSV_EMAIL_EXPORT_WORKFLOW_ID))) {
        await this.needsReAuthorization(CSV_EMAIL_EXPORT_WORKFLOW_ID);
        return;
      }

      const result = await this.launchService.run(CSV_EMAIL_EXPORT_WORKFLOW_ID, patientId);

      if (result.workflowRun.status !== 'Succeeded') {
        const errorMessage = result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.';
        if (indicatesReAuthorizationNeeded(errorMessage)) {
          await this.needsReAuthorization(CSV_EMAIL_EXPORT_WORKFLOW_ID);
          return;
        }
        this.emailError.set(errorMessage);
        return;
      }

      this.emailSuccess.set(true);
    } catch (err) {
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      const errorMessage = backendMessage
        ?? 'Could not reach FHIRBridge to send this email. Check your connection and try again.';
      if (indicatesReAuthorizationNeeded(errorMessage)) {
        await this.needsReAuthorization(CSV_EMAIL_EXPORT_WORKFLOW_ID);
        return;
      }
      this.emailError.set(errorMessage);
    } finally {
      this.isEmailingPatientInfo.set(false);
    }
  }

  // Cheap pre-check: does FHIRBridge currently have (or can it silently refresh) a usable token, without running
  // any pipeline? Defaults to "assume valid" on any error so a broken check never blocks the flow — the real /run
  // call right after is always the authoritative test either way.
  private async hasValidToken(workflowId: string): Promise<boolean> {
    try {
      return await this.launchService.hasValidToken(workflowId, this.patientId);
    } catch {
      return true;
    }
  }

  // Interprets the fetch failure: if it genuinely means "no usable token", delegates to needsReAuthorization().
  // Any other failure just shows the message and leaves state intact so the user can retry without redoing the
  // whole OAuth round trip.
  private async handleFetchFailure(errorMessage: string, workflowId: string): Promise<void> {
    if (!indicatesReAuthorizationNeeded(errorMessage)) {
      this.patientError.set(errorMessage);
      return;
    }

    await this.needsReAuthorization(workflowId);
  }

  // Clears the stale local/FHIRBridge state and redirects to MyChart using whichever hospital the user already
  // selected above — the "if not valid, goto MyChart, grant access, fetch data and display" leg. If the user hasn't
  // selected a hospital yet, there's nothing to redirect to, so this just asks them to pick one instead of guessing.
  // workflowId is whichever workflow's token-status/run call discovered the problem (this.workflowId for the list,
  // this.detailWorkflowId for a per-patient detail click) — the mint call below must target that same workflow,
  // since each has its own independent public-launch opt-in and (potentially) its own source connection.
  private async needsReAuthorization(workflowId: string): Promise<void> {
    void this.forgetSession();
    this.patientId = null;
    this.hasMyChartToken.set(false);
    this.lastConfirmedValidUtc.set(null);

    const selectedHospital = this.hospitals().find(hospital => hospital.id === this.selectedHospitalId());
    if (!selectedHospital) {
      this.patientError.set('Select a hospital above, then click Connect again to sign in.');
      return;
    }

    await this.redirectToMyChart(selectedHospital, workflowId);
  }

  onHospitalSearchChange(value: string): void {
    this.hospitalSearchQuery.set(value);
    if (this.hospitalSearchDebounceHandle) {
      clearTimeout(this.hospitalSearchDebounceHandle);
    }
    this.hospitalSearchDebounceHandle = setTimeout(() => void this.loadHospitals(), 250);
  }

  // Anonymous — no FHIRBridge session exists yet at this point, so this reads straight from FHIRBridge's public
  // ehr-mychart-endpoints listing.
  private async loadHospitals(): Promise<void> {
    this.isLoadingHospitals.set(true);
    try {
      const query = this.hospitalSearchQuery().trim();
      this.hospitals.set(await this.launchService.loadHospitals(query || undefined));
    } catch {
      this.hospitalSelectError.set('Could not load the hospital list from FHIRBridge.');
    } finally {
      this.isLoadingHospitals.set(false);
    }
  }

  // Step 1 of the flow: just marks which hospital the user intends to use — no network call, no redirect. The user
  // clicks Connect (step 2) after this, which is the only thing that actually acts on this selection.
  chooseHospital(endpoint: MyChartEndpoint): void {
    this.selectedHospitalId.set(endpoint.id);
    this.hospitalSelectError.set(null);
  }

  // Mints a launch context for the pre-selected hospital via FHIRBridge's anonymous public-patient-standalone-url
  // endpoint, and hands the browser off to MyChart's real authorization page. Must be a full top-level navigation,
  // not an HttpClient call: FHIRBridge's endpoint 302s onward, which an XHR/fetch can't complete interactively.
  private async redirectToMyChart(endpoint: MyChartEndpoint, workflowId: string): Promise<void> {
    this.isRedirectingToMyChart.set(true);
    this.hospitalSelectError.set(null);
    try {
      // callerId tells FHIRBridge's OAuthController.Callback to redirect the browser straight back to this exact
      // page (see ngOnInit) once the token exchange completes, instead of falling back to the source connection's
      // static PostLaunchRedirectUri — see InteractiveSourceAuthorizationService.CompleteAsync, where a caller-
      // supplied callerId always wins over that DB field. Origin + pathname only (no existing query/hash): the
      // callback appends its own workflowRunId/launchError/signedIn marker on top, and ngOnInit strips whatever
      // query string is present anyway. FHIRBridge validates the origin against Portal:AllowedOrigins before
      // honoring it (CallerIdOriginValidator) — this page's origin must be listed there.
      const callerId = `${window.location.origin}${window.location.pathname}`;
      const result = await this.launchService.mintLaunchUrl(workflowId, endpoint.id, callerId);
      window.location.href = result.launchUrl;
    } catch {
      this.isRedirectingToMyChart.set(false);
      this.hospitalSelectError.set(
        'Could not start sign-in for this hospital. The configured workflow may not be opted into public launch yet.',
      );
    }
  }

  // "Reset Token" — clears HealthApp's own remembered session AND FHIRBridge's actual cached token (so it genuinely
  // can't be reused, not just forgotten locally). The next Connect click will then correctly fail its check and
  // redirect to MyChart for a fresh sign-in — this is the button that proves step (e)'s "ask for token again"
  // branch on demand, as opposed to just clicking Connect again with a still-valid token (which fetches immediately).
  resetToken(): void {
    void this.forgetSession();
    void this.discardFhirBridgeToken();
    this.patientId = null;
    this.lastConfirmedValidUtc.set(null);
    this.fetchedPatients.set(null);
    this.patientError.set(null);
    this.hasMyChartToken.set(false);
    this.selectedPatientId.set(null);
    this.patientDetail.set(null);
    this.patientDetailError.set(null);
    this.downloadError.set(null);
    this.emailError.set(null);
    this.emailSuccess.set(false);
  }

  private async discardFhirBridgeToken(): Promise<void> {
    try {
      await this.launchService.discardToken(this.workflowId, this.patientId);
    } catch {
      // Non-fatal — worst case FHIRBridge's cache still has the old token, which the next /run attempt would just
      // successfully reuse (same as if Reset Token had never been clicked); nothing is left in a broken state.
    }
  }
}
