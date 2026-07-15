import { Component, NgZone, OnInit, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  FetchedPatient,
  MyChartEndpoint,
  PatientStandaloneLaunchService,
  extractFetchedPatients,
  indicatesReAuthorizationNeeded,
} from './core/services/patient-standalone-launch.service';

@Component({
  selector: 'app-launch-standalone-patient',
  standalone: true,
  imports: [DatePipe, MatCardModule, MatProgressSpinnerModule],
  providers: [PatientStandaloneLaunchService],
  templateUrl: './launch-standalone-patient.html',
  styleUrl: './launch-standalone-patient.scss',
})
export class LaunchStandalonePatientComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  // Purely informational badge — never gates whether the Connect button is shown. The real, authoritative check is
  // always the next actual /run attempt (see fetchPatient); this is just a "last known good" hint carried over from
  // HealthApp's own remembered session or the most recent successful fetch.
  readonly hasMyChartToken = signal(false);
  readonly lastConfirmedValidUtc = signal<string | null>(null);

  readonly launchError = signal<string | null>(null);

  readonly isFetchingPatient = signal(false);
  readonly patientError = signal<string | null>(null);
  readonly fetchedPatients = signal<FetchedPatient[] | null>(null);

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

  constructor(
    private readonly launchService: PatientStandaloneLaunchService,
    private readonly ngZone: NgZone,
  ) {}

  ngOnInit(): void {
    // FHIRBridge's OAuth callback redirects back here with one of three markers (see OAuthController.Callback):
    // ?workflowRunId=... when its own no-criteria convenience run got a real run id, ?launchError=... when that run
    // was attempted but threw, or ?signedIn=1 when the convenience run was deliberately skipped altogether (the
    // common case for a Patient Standalone sign-in with no upfront search criteria). All three only ever appear
    // once the token exchange itself has already succeeded. Read straight off window.location rather than
    // ActivatedRoute: this app never uses a real <router-outlet> for this content (app.html renders it via a plain
    // selectedDemoType() @if/@else-if), so ActivatedRoute here could race Angular Router's own async
    // initialization on the exact page load right after this full-page redirect back from MyChart.
    const params = new URLSearchParams(window.location.search);
    const workflowRunId = params.get('workflowRunId');
    const launchError = params.get('launchError');
    const signedIn = params.get('signedIn');
    if (workflowRunId || launchError || signedIn) {
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
      return;
    }

    // The hospital list is always visible from the start (step 1 of the flow), regardless of whether a remembered
    // session exists — the user may still want to pick/change the target hospital before clicking Connect.
    void this.loadHospitals();
    void this.checkRememberedSession();
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
      if (!(await this.hasValidToken())) {
        await this.needsReAuthorization();
        return;
      }

      const result = await this.launchService.run(this.patientId);

      if (result.workflowRun.status === 'Succeeded') {
        this.fetchedPatients.set(extractFetchedPatients(result));
        this.hasMyChartToken.set(true);
        // Clears any stale "workflow_failed" banner from FHIRBridge's own no-criteria convenience run — that run
        // failing is an expected, benign artifact of the Standalone launch flow, not a real problem.
        this.launchError.set(null);
        void this.rememberSession();
        return;
      }

      await this.handleFetchFailure(result.workflowRun.errorMessage ?? 'The workflow run failed for an unknown reason.');
    } catch (err) {
      // A failure resolved before the run even starts (e.g. no token cached at all, or an expired one) throws past
      // the orchestrator and surfaces here as an HttpErrorResponse with a bare {error: "..."} body.
      const backendMessage = err instanceof HttpErrorResponse && typeof err.error?.error === 'string'
        ? err.error.error
        : null;
      await this.handleFetchFailure(
        backendMessage ?? 'Could not reach FHIRBridge to trigger the workflow. Check your connection and try again.',
      );
    } finally {
      this.isFetchingPatient.set(false);
    }
  }

  // Cheap pre-check: does FHIRBridge currently have (or can it silently refresh) a usable token, without running
  // any pipeline? Defaults to "assume valid" on any error so a broken check never blocks the flow — the real /run
  // call right after is always the authoritative test either way.
  private async hasValidToken(): Promise<boolean> {
    try {
      return await this.launchService.hasValidToken(this.patientId);
    } catch {
      return true;
    }
  }

  // Interprets the fetch failure: if it genuinely means "no usable token", delegates to needsReAuthorization().
  // Any other failure just shows the message and leaves state intact so the user can retry without redoing the
  // whole OAuth round trip.
  private async handleFetchFailure(errorMessage: string): Promise<void> {
    if (!indicatesReAuthorizationNeeded(errorMessage)) {
      this.patientError.set(errorMessage);
      return;
    }

    await this.needsReAuthorization();
  }

  // Clears the stale local/FHIRBridge state and redirects to MyChart using whichever hospital the user already
  // selected above — the "if not valid, goto MyChart, grant access, fetch data and display" leg. If the user hasn't
  // selected a hospital yet, there's nothing to redirect to, so this just asks them to pick one instead of guessing.
  private async needsReAuthorization(): Promise<void> {
    void this.forgetSession();
    this.patientId = null;
    this.hasMyChartToken.set(false);
    this.lastConfirmedValidUtc.set(null);

    const selectedHospital = this.hospitals().find(hospital => hospital.id === this.selectedHospitalId());
    if (!selectedHospital) {
      this.patientError.set('Select a hospital above, then click Connect again to sign in.');
      return;
    }

    await this.redirectToMyChart(selectedHospital);
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
  private async redirectToMyChart(endpoint: MyChartEndpoint): Promise<void> {
    this.isRedirectingToMyChart.set(true);
    this.hospitalSelectError.set(null);
    try {
      const result = await this.launchService.mintLaunchUrl(endpoint.id);
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
  }

  private async discardFhirBridgeToken(): Promise<void> {
    try {
      await this.launchService.discardToken(this.patientId);
    } catch {
      // Non-fatal — worst case FHIRBridge's cache still has the old token, which the next /run attempt would just
      // successfully reuse (same as if Reset Token had never been clicked); nothing is left in a broken state.
    }
  }
}
