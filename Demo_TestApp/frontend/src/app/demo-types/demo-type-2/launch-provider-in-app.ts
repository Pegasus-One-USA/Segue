import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, input, output, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Patient } from './core/models/patient.model';
import { PatientService } from './core/services/patient.service';
import { FHIRBRIDGE_BASE_URL, PROVIDER_LAUNCH_CONTEXT } from './core/config/launch.config';

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

  readonly age = computed(() => {
    const dateOfBirth = this.patient()?.dateOfBirth;
    return dateOfBirth ? this.calculateAge(dateOfBirth) : null;
  });

  constructor(private readonly patientService: PatientService) {}

  ngOnInit(): void {
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
      const launchUrl =
        `${FHIRBRIDGE_BASE_URL}/api/v1/oauth/launch/${PROVIDER_LAUNCH_CONTEXT}` +
        `?iss=${encodeURIComponent(iss)}&launch=${encodeURIComponent(launch)}`;
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
