import { Component, OnInit, computed, input, output, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PATIENT_STANDALONE_PATH } from '../../core/routes';

const BACKEND_BASE_URL = environment.healthAppBase;

// The real Patient Standalone launch flow (hospital picker + SMART OAuth redirect) lives on its own path/component
// (see LaunchStandalonePatientComponent) — this dashboard mockup just hands off to it via a full page navigation.
// The Patient role persists across that reload via sessionStorage (see app.ts's AUTH_ROLE_STORAGE_KEY), so no
// query param is needed to re-select anything on the way back — app.html's isOnPatientStandaloneLaunchPath check
// is all that's needed to tell the two situations apart once role() is 'Patient' either way.
const PATIENT_STANDALONE_LAUNCH_URL = PATIENT_STANDALONE_PATH;

interface PatientCard {
  resourceId: string;
  recordCreatedOn: string;
  fullName: string | null;
  firstName: string | null;
  middleName: string | null;
  lastName: string | null;
  gender: string | null;
  legalSex: string | null;
  sexForClinicalUse: string | null;
  pronouns: string | null;
  dateOfBirth: string | null;
  age: string | null;
  maritalStatus: string | null;
  patientStatus: string | null;
  deceased: string | null;
}

@Component({
  selector: 'app-patient-standalone',
  templateUrl: './patient-standalone.html',
  styleUrl: './patient-standalone.scss'
})
export class PatientStandaloneComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  protected readonly patient = {
    name: 'Sarah Whitfield',
    address: '482 Maple Grove Ave, Denver, CO 80203',
    initials: 'SW',
  };

  protected readonly vitals = {
    heartRate: 72,
    bloodPressureSystolic: 118,
    bloodPressureDiastolic: 76,
    steps: 6842,
  };

  // Records screen (card carousel over every ingested patient)
  protected readonly screen = signal<'home' | 'records'>('home');
  protected readonly records = signal<PatientCard[]>([]);
  protected readonly recordIndex = signal(0);
  protected readonly recordsError = signal('');
  protected readonly currentRecord = computed(() => this.records()[this.recordIndex()] ?? null);
  protected readonly currentRecordFields = computed(() => {
    const record = this.currentRecord();
    if (!record) {
      return [];
    }

    return [
      { label: 'Full Name', value: record.fullName },
      { label: 'First Name', value: record.firstName },
      { label: 'Middle Name', value: record.middleName },
      { label: 'Last Name', value: record.lastName },
      { label: 'Gender', value: record.gender },
      { label: 'Legal Sex', value: record.legalSex },
      { label: 'Sex for Clinical Use', value: record.sexForClinicalUse },
      { label: 'Pronouns', value: record.pronouns },
      { label: 'Date of Birth', value: record.dateOfBirth },
      { label: 'Age', value: record.age },
      { label: 'Marital Status', value: record.maritalStatus },
      { label: 'Patient Status', value: record.patientStatus },
      { label: 'Deceased', value: record.deceased },
    ].map((field) => ({ label: field.label, value: field.value ?? '—' }));
  });

  constructor(
    private readonly http: HttpClient,
    private readonly route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    // The OAuth "Connect" flow redirects back here (often into a fresh tab, per FHIRBridge's configured
    // PostLaunchRedirectUri) with ?workflowRunId=<id> once the launched workflow has finished pulling and
    // storing the patient's data — jump straight to the Records screen instead of leaving the user on Home.
    const workflowRunId = this.route.snapshot.queryParamMap.get('workflowRunId');
    if (workflowRunId) {
      void this.openRecords();
    }
  }

  openConnectFlow(): void {
    window.location.href = PATIENT_STANDALONE_LAUNCH_URL;
  }

  async openRecords(): Promise<void> {
    this.recordsError.set('');

    try {
      const records = await firstValueFrom(
        this.http.get<PatientCard[]>(`${BACKEND_BASE_URL}/api/patients`, { withCredentials: true })
      );
      this.records.set(records);
      this.recordIndex.set(0);
      this.screen.set('records');
    } catch {
      this.recordsError.set('Could not load patient records.');
    }
  }

  closeRecords(): void {
    this.screen.set('home');
  }

  previousRecord(): void {
    this.recordIndex.update((i) => Math.max(0, i - 1));
  }

  nextRecord(): void {
    this.recordIndex.update((i) => Math.min(this.records().length - 1, i + 1));
  }
}
