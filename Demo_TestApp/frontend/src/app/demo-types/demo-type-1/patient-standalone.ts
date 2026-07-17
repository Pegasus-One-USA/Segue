import { Component, OnInit, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PATIENT_STANDALONE_PATH } from '../../core/routes';

const BACKEND_BASE_URL = environment.healthAppBase;

// The real Patient Standalone launch flow (hospital picker + SMART OAuth redirect) lives on its own path/component
// (see LaunchStandalonePatientComponent) — this dashboard mockup just hands off to it via a full page navigation.
// DemoType=1 is Patient_Standalone's seeded row id (see HealthAppDbContext's DemoTypeEntity.HasData) — it's what
// makes app.ts's loadDemoTypes() lock onto and select the Patient_Standalone Login Type once this lands there.
const PATIENT_STANDALONE_LAUNCH_URL = `${PATIENT_STANDALONE_PATH}?DemoType=1`;

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
  imports: [FormsModule],
  templateUrl: './patient-standalone.html',
  styleUrl: './patient-standalone.scss'
})
export class PatientStandaloneComponent implements OnInit {
  readonly role = input<string | null>(null);
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

  // Admin settings (persisted server-side — see WorkflowSettingsEntity). PatientWorkflowId/PatientDetailWorkflowId/
  // PatientBaseUrl are Patient Standalone's own FHIRBridge connection points, formerly a gitignored per-developer
  // local file (standalone-launch.config.ts) — now editable here like WorkflowUrl already was. The two workflow
  // ids are independent: the list fetch uses PatientWorkflowId, the per-patient detail fetch (clicking a row in
  // the fetched list) uses PatientDetailWorkflowId, each with its own FHIRBridge public-launch opt-in.
  protected readonly settingsOpen = signal(false);
  protected readonly workflowUrlDraft = signal('');
  protected readonly patientWorkflowIdDraft = signal('');
  protected readonly patientDetailWorkflowIdDraft = signal('');
  protected readonly patientBaseUrlDraft = signal('');
  protected readonly settingsError = signal('');

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

  async openSettings(): Promise<void> {
    this.settingsError.set('');

    try {
      const current = await firstValueFrom(
        this.http.get<{
          workflowUrl: string;
          patientWorkflowId: string;
          patientDetailWorkflowId: string;
          patientBaseUrl: string;
        }>(
          `${BACKEND_BASE_URL}/api/settings`,
          { withCredentials: true }
        )
      );
      this.workflowUrlDraft.set(current.workflowUrl);
      this.patientWorkflowIdDraft.set(current.patientWorkflowId);
      this.patientDetailWorkflowIdDraft.set(current.patientDetailWorkflowId);
      this.patientBaseUrlDraft.set(current.patientBaseUrl);
      this.settingsOpen.set(true);
    } catch {
      this.settingsError.set('Could not load settings.');
    }
  }

  closeSettings(): void {
    this.settingsOpen.set(false);
  }

  async saveSettings(): Promise<void> {
    try {
      await firstValueFrom(
        this.http.post(
          `${BACKEND_BASE_URL}/api/settings`,
          {
            workflowUrl: this.workflowUrlDraft(),
            patientWorkflowId: this.patientWorkflowIdDraft(),
            patientDetailWorkflowId: this.patientDetailWorkflowIdDraft(),
            patientBaseUrl: this.patientBaseUrlDraft(),
          },
          { withCredentials: true }
        )
      );
      this.settingsOpen.set(false);
    } catch {
      this.settingsError.set('Could not save settings.');
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
