import { Component, OnInit, input, output, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import { FHIRBRIDGE_BASE_URL, STANDALONE_LAUNCH_CONTEXT } from './core/config/standalone-launch.config';

const BACKEND_BASE_URL = 'http://localhost:5500';

interface Hospital {
  id: number;
  name: string;
  organizationId: number;
}

@Component({
  selector: 'app-launch-standalone-provider',
  standalone: true,
  imports: [MatCardModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './launch-standalone-provider.html',
  styleUrl: './launch-standalone-provider.scss',
})
export class LaunchStandaloneProviderComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly hospitals = signal<Hospital[]>([]);
  readonly isLoadingHospitals = signal(true);

  // Set once FHIRBridge redirects back here after the provider signs into Epic — see ngOnInit. Distinguishes
  // "just landed, pick a hospital" from "token acquired, ready to fetch" without a page-level route change.
  readonly hasEpicToken = signal(false);
  readonly launchError = signal<string | null>(null);

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
      this.hasEpicToken.set(true);
      this.launchError.set(launchError);
      return;
    }

    void this.loadHospitals();
  }

  private async loadHospitals(): Promise<void> {
    try {
      const hospitals = await firstValueFrom(
        this.http.get<Hospital[]>(`${BACKEND_BASE_URL}/api/hospitals`, { withCredentials: true }),
      );
      this.hospitals.set(hospitals);
    } finally {
      this.isLoadingHospitals.set(false);
    }
  }

  // Every hospital currently resolves to the same Epic sandbox launch context — FHIRBridge doesn't yet have a
  // per-hospital EhrEndpoint wired to this flow, so the picker is cosmetic until that's configured. This must
  // be a full top-level navigation (not an HttpClient call): FHIRBridge's endpoint 302s to Epic's real
  // authorization page, which an XHR/fetch can't complete interactively.
  selectHospital(_hospital: Hospital): void {
    window.location.href = `${FHIRBRIDGE_BASE_URL}/api/v1/oauth/authorize/${STANDALONE_LAUNCH_CONTEXT}`;
  }

  // Phase 2: wire to the "patient list" workflow's result once it's provided.
  fetchPatientList(): void {}
}
