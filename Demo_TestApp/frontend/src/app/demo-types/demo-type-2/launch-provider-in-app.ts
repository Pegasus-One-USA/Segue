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

/** Matches Demo_TestApp's GET /api/provider-in-app-launch-context response. */
interface ProviderInAppLaunchContext {
  providerLaunchContext: string;
}

// A blank/placeholder token is indistinguishable from a real one syntactically — FHIRBridge's own launch
// endpoint would 404 on either, so both are treated as "ask an admin to set one" rather than attempted.
function isConfiguredValue(value: string | null | undefined): boolean {
  return !!value && value.trim().length > 0 && !value.includes('REPLACE_WITH_REAL');
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

  // Admin-editable via the unified Admin Settings screen (see AdminSettingsComponent) — persisted on
  // Demo_TestApp's own backend (WorkflowSettingsEntity), read at runtime rather than baked in at build time.
  // Falls back to whatever's in launch.config.ts (the pre-existing, gitignored, compile-time mechanism) until
  // an admin sets this through the UI, so a repo that already filled in that file keeps working unchanged.
  private providerLaunchContext = PROVIDER_LAUNCH_CONTEXT;
  private launchContextLoadPromise: Promise<void> | null = null;

  readonly age = computed(() => {
    const dateOfBirth = this.patient()?.dateOfBirth;
    return dateOfBirth ? this.calculateAge(dateOfBirth) : null;
  });

  constructor(
    private readonly patientService: PatientService,
    private readonly http: HttpClient,
  ) {}

  // Loads the admin-configured launch-context token exactly once per component lifetime (memoized via
  // launchContextLoadPromise) — ngOnInit awaits this before ever building the redirect URL, so a launch can
  // never race ahead and use the config-file fallback by accident.
  private ensureLaunchContextLoaded(): Promise<void> {
    if (!this.launchContextLoadPromise) {
      this.launchContextLoadPromise = this.loadLaunchContext();
    }
    return this.launchContextLoadPromise;
  }

  private async loadLaunchContext(): Promise<void> {
    try {
      const current = await firstValueFrom(
        this.http.get<ProviderInAppLaunchContext>(
          `${HEALTHAPP_BACKEND_BASE_URL}/api/provider-in-app-launch-context`,
          { withCredentials: true },
        ),
      );
      if (isConfiguredValue(current.providerLaunchContext)) {
        this.providerLaunchContext = current.providerLaunchContext;
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

    // Fresh EHR launch: Epic redirected here with iss+launch (no FHIRBridge round-trip has happened yet). Hand the
    // browser off to FHIRBridge's launch endpoint — it validates iss, completes the OAuth flow with Epic, runs the
    // configured workflow, and redirects back here with ?workflowRunId=... once done (see PatientService.getPatient,
    // which reads that param on the way back). This must be a full top-level navigation, not an HttpClient call:
    // FHIRBridge's endpoint 302s to Epic's real authorization page, which an XHR/fetch can't complete interactively.
    const iss = params.get('iss');
    const launch = params.get('launch');
    if (iss && launch) {
      await this.ensureLaunchContextLoaded();
      // Passes our own current origin as a live callerId override, so FHIRBridge redirects back here after OAuth
      // completes regardless of which environment (local/staging/production) is actually running this page — the
      // static providerLaunchContext token can't otherwise reflect that per-environment. See OAuthController.LaunchPipeline.
      const callerId = `${window.location.origin}/launchproviderinapp`;
      const launchUrl =
        `${FHIRBRIDGE_BASE_URL}/api/v1/oauth/launch/${this.providerLaunchContext}` +
        `?iss=${encodeURIComponent(iss)}&launch=${encodeURIComponent(launch)}` +
        `&callerId=${encodeURIComponent(callerId)}`;
      window.location.href = launchUrl;
      return;
    }

    // FHIRBridge sets this instead of ?workflowRunId= when the workflow it triggered after OAuth threw —
    // surface it rather than silently falling back to mock data, which would look like a working demo.
    const launchError = params.get('launchError');
    if (launchError) {
      this.launchError.set(launchError);
      this.isPatientLoading.set(false);
      return;
    }

    this.patientService.getPatient().subscribe({
      next: (patient) => {
        this.patient.set(patient);
        this.isPatientLoading.set(false);
      },
      error: (error: unknown) => {
        this.launchError.set(error instanceof Error ? error.message : 'Failed to load patient data.');
        this.isPatientLoading.set(false);
      },
    });
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
