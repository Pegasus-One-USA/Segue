import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, input, output, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import { Patient } from './core/models/patient.model';
import { PatientService } from './core/services/patient.service';
import { FHIRBRIDGE_BASE_URL, PROVIDER_LAUNCH_CONTEXT } from './core/config/launch.config';
import { environment } from '../../../environments/environment';

/** Matches Demo_TestApp's GET /api/provider-in-app-launch-context response. standaloneBaseUrl is
 *  WorkflowSettingsEntity.StandaloneBaseUrl — deliberately reused rather than a dedicated field, since
 *  Provider_InApp and Provider_Standalone both launch against the same FHIRBridge deployment. */
interface ProviderInAppLaunchContext {
  providerLaunchContext: string;
  standaloneBaseUrl: string;
}

// A blank/placeholder token is indistinguishable from a real one syntactically — FHIRBridge's own launch
// endpoint would 404 on either, so both are treated as "ask an admin to set one" rather than attempted.
function isConfiguredValue(value: string | null | undefined): boolean {
  return !!value && value.trim().length > 0 && !value.includes('REPLACE_WITH_REAL');
}

// Same "not configured" shape as isConfiguredValue above, but for StandaloneBaseUrl — guards against both an
// empty value and the literal placeholder left in appsettings.json's DefaultWorkflowSettings:StandaloneBaseUrl
// (REPLACE_WITH_PUBLIC_URL) for any environment that hasn't set a real one yet.
function isConfiguredBaseUrl(value: string | null | undefined): boolean {
  return !!value && value.trim().length > 0 && value !== 'http://REPLACE_WITH_PUBLIC_URL';
}

// HealthApp's own backend (Demo_TestApp), not FHIRBridge.
const HEALTHAPP_BACKEND_BASE_URL = environment.healthAppBase;

@Component({
  selector: 'app-launch-provider-in-app',
  standalone: true,
  imports: [DatePipe, MatCardModule, MatChipsModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './launch-provider-in-app.html',
  styleUrl: './launch-provider-in-app.scss'
})
export class LaunchProviderInAppComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly patient = signal<Patient | null>(null);
  readonly isPatientLoading = signal(true);
  readonly launchError = signal<string | null>(null);
  // Set only when an embedded (iframe) EHR launch couldn't be auto-broken-out of the frame (a sandboxed
  // cross-origin frame blocks programmatic top-level navigation). Holds the FHIRBridge launch URL so the template
  // can render a full-page "Continue" link with target="_top" — the user's click is a gesture the browser does
  // allow to navigate the top window, completing the break-out the automatic attempt couldn't.
  readonly breakoutUrl = signal<string | null>(null);
  // Vendor-aware "Environment" chip. Defaults neutral; set to the launching EHR's name once known from the
  // inbound launch's iss (persisted so the post-OAuth return leg, which has no iss, still shows the right vendor).
  readonly environmentLabel = signal('FHIRBridge');
  // True when the auto-fetch-by-workflow-id path produced no patient (or errored) — the template then shows a
  // "Refresh" button so the user can retry the fetch by hand.
  readonly canRetry = signal(false);

  // Persisted across the launch round-trip so the "fetch latest by workflow id" call can route to the right
  // vendor's workflow (eCW vs Epic) the same way the backend mint does — the return leg / a fresh view has no iss.
  private static readonly ISS_STORAGE_KEY = 'hb_provider_inapp_iss';

  // Admin-editable via the unified Admin Settings screen (see AdminSettingsComponent) — persisted on
  // Demo_TestApp's own backend (WorkflowSettingsEntity), read at runtime rather than baked in at build time.
  // Falls back to whatever's in launch.config.ts (the pre-existing, gitignored, compile-time mechanism) until
  // an admin sets this through the UI, so a repo that already filled in that file keeps working unchanged.
  private providerLaunchContext = PROVIDER_LAUNCH_CONTEXT;
  // Same admin-editable/fallback story as providerLaunchContext above — falls back to the build-time
  // FHIRBRIDGE_BASE_URL constant (only ever correct when the browser and FHIRBridge Api share a host, e.g. local
  // dev) until loadLaunchContext() resolves the admin-configured WorkflowSettingsEntity.StandaloneBaseUrl
  // (reused from Provider_Standalone rather than a dedicated field — see ProviderInAppLaunchContext).
  private baseUrl = FHIRBRIDGE_BASE_URL;
  private launchContextLoadPromise: Promise<void> | null = null;

  readonly age = computed(() => {
    const dateOfBirth = this.patient()?.dateOfBirth;
    return dateOfBirth ? this.calculateAge(dateOfBirth) : null;
  });

  // Per-resource-type fetch counts for the current run (Patient, Condition, Observation, …) — from launch-result.
  readonly resourceCounts = signal<Record<string, number>>({});
  // Template-friendly, name-sorted list of {type, count}, plus the grand total, for the "Resources Fetched" card.
  readonly resourceTypeList = computed(() =>
    Object.entries(this.resourceCounts())
      .map(([type, count]) => ({ type, count }))
      .sort((a, b) => a.type.localeCompare(b.type)),
  );
  readonly totalResourceCount = computed(() =>
    this.resourceTypeList().reduce((sum, entry) => sum + entry.count, 0),
  );

  constructor(
    private readonly patientService: PatientService,
    private readonly http: HttpClient,
  ) {}

  // Loads the admin-configured launch-context token exactly once per component lifetime (memoized via
  // launchContextLoadPromise) — ngOnInit awaits this before ever building the redirect URL, so a launch can
  // never race ahead and use the config-file fallback by accident.
  private ensureLaunchContextLoaded(iss?: string | null): Promise<void> {
    if (!this.launchContextLoadPromise) {
      this.launchContextLoadPromise = this.loadLaunchContext(iss);
    }
    return this.launchContextLoadPromise;
  }

  private async loadLaunchContext(iss?: string | null): Promise<void> {
    try {
      // Forward the launching EHR's iss so the backend mints against the right vendor's workflow — an eCW iss
      // (host *.ecwcloud.com) selects the separate eCW Provider EMR workflow id, otherwise the Epic one (see
      // Demo_TestApp/backend's /api/provider-in-app-launch-context). Omitted on the post-OAuth return leg, which
      // only needs the vendor-agnostic base URL back.
      const contextUrl = `${HEALTHAPP_BACKEND_BASE_URL}/api/provider-in-app-launch-context`
        + (iss ? `?iss=${encodeURIComponent(iss)}` : '');
      const current = await firstValueFrom(
        this.http.get<ProviderInAppLaunchContext>(contextUrl, { withCredentials: true }),
      );
      if (isConfiguredValue(current.providerLaunchContext)) {
        this.providerLaunchContext = current.providerLaunchContext;
      }
      if (isConfiguredBaseUrl(current.standaloneBaseUrl)) {
        this.baseUrl = current.standaloneBaseUrl;
      }
    } catch {
      // Non-fatal — falls back to whatever's in launch.config.ts (possibly still the placeholder, which every
      // caller below already guards against via isConfiguredValue).
    }
  }

  async ngOnInit(): Promise<void> {
    // Read query params straight off the browser URL rather than via ActivatedRoute: this app has no
    // <router-outlet> (see app.routes.ts) — components are swapped by signals, not route activation — so
    // ActivatedRoute.snapshot is only reliably populated once the Router's own (asynchronous) initial
    // navigation resolves, which is not guaranteed to happen before this ngOnInit runs. window.location.search
    // reflects the current URL synchronously, with no such race.
    const params = new URLSearchParams(window.location.search);

    // Vendor-aware Environment chip: the inbound launch leg carries the launching EHR's iss; derive the vendor from
    // it and persist so the post-OAuth return leg (workflowRunId, no iss) keeps showing the right one. host
    // *.ecwcloud.com is eCW; otherwise Epic — mirrors the backend mint's own iss routing.
    const VENDOR_STORAGE_KEY = 'hb_provider_inapp_vendor';
    const issForVendor = params.get('iss');
    if (issForVendor) {
      sessionStorage.setItem(
        VENDOR_STORAGE_KEY,
        /ecwcloud\.com/i.test(issForVendor) ? 'eClinicalWorks (eCW)' : 'Epic Sandbox',
      );
      sessionStorage.setItem(LaunchProviderInAppComponent.ISS_STORAGE_KEY, issForVendor);
    }
    const storedVendor = sessionStorage.getItem(VENDOR_STORAGE_KEY);
    if (storedVendor) {
      this.environmentLabel.set(storedVendor);
    }

    // Fresh EHR launch: Epic redirected here with iss+launch (no FHIRBridge round-trip has happened yet). Hand the
    // browser off to FHIRBridge's launch endpoint — it validates iss, completes the OAuth flow with Epic, runs the
    // configured workflow, and redirects back here with ?workflowRunId=... once done (see PatientService.getPatient,
    // which reads that param on the way back). This must be a full top-level navigation, not an HttpClient call:
    // FHIRBridge's endpoint 302s to Epic's real authorization page, which an XHR/fetch can't complete interactively.
    const iss = params.get('iss');
    const launch = params.get('launch');
    if (iss && launch) {
      await this.ensureLaunchContextLoaded(iss);
      // Passes our own current origin as a live callerId override, so FHIRBridge redirects back here after OAuth
      // completes regardless of which environment (local/staging/production) is actually running this page — the
      // static providerLaunchContext token can't otherwise reflect that per-environment. See OAuthController.LaunchPipeline.
      const callerId = `${window.location.origin}/launchproviderinapp`;
      const launchUrl =
        `${this.baseUrl}/api/v1/oauth/launch/${this.providerLaunchContext}` +
        `?iss=${encodeURIComponent(iss)}&launch=${encodeURIComponent(launch)}` +
        `&callerId=${encodeURIComponent(callerId)}`;

      // Break out of an embedded (Epic/eCW "Embedded" display mode) iframe: this redirect 302s to the EHR's own
      // authorize page, which refuses to render inside a frame (X-Frame-Options / frame-ancestors), and hb_session
      // (SameSite=Lax) isn't sent from a cross-site frame — so the whole OAuth round-trip must happen at the top
      // level. Navigating window.top frame-busts a normal (non-sandboxed) embed; for a plain non-embedded launch
      // window.top === window.self, so this is identical to a same-frame redirect. Assigning location.href on a
      // cross-origin ancestor is a permitted navigation (unlike reading it); a sandboxed frame that blocks even
      // that throws, so fall back to a user-clickable full-page link (a gesture the browser does allow).
      const topWindow = window.top ?? window;
      try {
        topWindow.location.href = launchUrl;
      } catch {
        this.breakoutUrl.set(launchUrl);
        this.isPatientLoading.set(false);
      }
      return;
    }

    // FHIRBridge sets this instead of ?workflowRunId= when the workflow it triggered after OAuth threw —
    // surface it rather than silently falling back to mock data, which would look like a working demo.
    const launchError = params.get('launchError');
    const workflowRunId = params.get('workflowRunId');

    // Consume both once, then strip them from the visible URL — same reasoning as the Standalone components'
    // own history.replaceState calls (see launch-standalone-provider.ts/launch-standalone-patient.ts): without
    // this, ?workflowRunId=... sits in the address bar indefinitely (this app has no router to otherwise clean
    // it up), and app.ts's login() deliberately preserves the current query string across a subsequent login for
    // this exact role (needed so a real iss+launch survives logging in first) — which also preserves a STALE
    // workflowRunId across a DIFFERENT HealthApp account's later login on the same tab. That account would then
    // silently be shown whichever patient the ORIGINAL account's real EHR launch fetched (FHIRBridge's launch-
    // result endpoint has no per-caller ownership check on the run id), never seeing an error or a fresh fetch.
    if (launchError || workflowRunId) {
      window.history.replaceState(null, '', window.location.pathname);
    }

    if (launchError) {
      this.launchError.set(launchError);
      this.isPatientLoading.set(false);
      return;
    }

    // Account-linking check — see Demo_TestApp/backend's AccountContextLinkEntity remarks for what this
    // enforces (and why it lives here, not in FHIRBridge's own binding table): this app has ground-truth
    // knowledge of which HealthApp account is logged in, so it can reject a different account claiming a
    // patient another account already linked. A no-op (ok: true) when there's no session to check against at
    // all (the genuinely embedded-iframe case) or the request otherwise can't be completed — never blocks the
    // existing flow on a failure of this check itself, only on an actual, confirmed mismatch.
    const linkOk = await this.checkAccountContextLink(workflowRunId);
    if (!linkOk.ok) {
      this.launchError.set(linkOk.message ?? 'This account is already linked to a different patient.');
      this.isPatientLoading.set(false);
      return;
    }

    if (workflowRunId) {
      this.loadPatientByRunId(workflowRunId);
      return;
    }

    // No specific run id on the URL (this page was opened without a fresh launch or a post-OAuth return). Instead of
    // showing mock data, auto-fetch the workflow's latest fetched patient BY WORKFLOW ID — no re-launch needed
    // (FHIRBridge already holds the data + a refreshable token). If nothing comes back, refresh() flips canRetry so
    // the user gets a "Refresh" button to try again by hand.
    await this.refresh();
  }

  private loadPatientByRunId(workflowRunId: string): void {
    this.patientService.getPatient(workflowRunId).subscribe({
      next: (outcome) => {
        this.patient.set(outcome.patient);
        this.resourceCounts.set(outcome.resourceCounts);
        this.isPatientLoading.set(false);
        this.canRetry.set(false);
      },
      error: (error: unknown) => {
        this.launchError.set(error instanceof Error ? error.message : 'Failed to load patient data.');
        this.isPatientLoading.set(false);
        this.canRetry.set(true);
      },
    });
  }

  // Auto-fetch (or, via the Refresh button, re-fetch) the ProviderInApp workflow's latest fetched patient by
  // workflow id — the app never needs the specific run id, and this never triggers a fresh EHR launch. Routes to
  // the right vendor via the persisted iss (see ISS_STORAGE_KEY), the same way the backend mint does.
  async refresh(): Promise<void> {
    this.isPatientLoading.set(true);
    this.launchError.set(null);
    this.canRetry.set(false);

    const iss = sessionStorage.getItem(LaunchProviderInAppComponent.ISS_STORAGE_KEY);
    const url = `${HEALTHAPP_BACKEND_BASE_URL}/api/provider-in-app-latest-result`
      + (iss ? `?iss=${encodeURIComponent(iss)}` : '');
    try {
      const latest = await firstValueFrom(
        this.http.get<{ workflowRunId: string | null; vendor?: string }>(url, { withCredentials: true }),
      );
      // Vendor determined server-side BY THE WORKFLOW ID — authoritative across any browser (unlike the
      // iss-derived sessionStorage guess), so prefer it and persist it for this session's other legs.
      if (latest.vendor) {
        this.environmentLabel.set(latest.vendor);
        sessionStorage.setItem('hb_provider_inapp_vendor', latest.vendor);
      }
      if (latest.workflowRunId) {
        this.loadPatientByRunId(latest.workflowRunId);
      } else {
        // Workflow has no run with a patient yet (or none is configured) — offer a manual retry rather than mock.
        this.isPatientLoading.set(false);
        this.canRetry.set(true);
      }
    } catch {
      this.isPatientLoading.set(false);
      this.canRetry.set(true);
    }
  }

  private async checkAccountContextLink(workflowRunId: string | null): Promise<{ ok: boolean; message?: string }> {
    if (!workflowRunId) {
      return { ok: true };
    }
    try {
      return await firstValueFrom(
        this.http.get<{ ok: boolean; message?: string }>(
          `${HEALTHAPP_BACKEND_BASE_URL}/api/account-context-link/check`,
          { params: { workflowRunId, audienceType: 'ehrLaunch' }, withCredentials: true },
        ),
      );
    } catch {
      // Non-fatal — see this method's only caller's remarks. A failed check must never block a launch that
      // would otherwise have succeeded.
      return { ok: true };
    }
  }

  displayValue(value: string | null | undefined): string {
    return value && value.trim().length > 0 ? value : 'Not Available';
  }

  private calculateAge(dateOfBirth: string): number {
    const dob = new Date(dateOfBirth);
    const today = new Date();
    let age = today.getFullYear() - dob.getFullYear();
    const monthDiff = today.getMonth() - dob.getMonth();

    if (monthDiff < 0 || (monthDiff === 0 && today.getDate() < dob.getDate())) {
      age--;
    }

    return age;
  }
}
