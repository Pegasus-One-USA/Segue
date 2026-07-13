import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

const BACKEND_BASE_URL = 'http://localhost:5500';

interface Hospital {
  id: number;
  name: string;
  organizationId: number;
}

interface LoginResponse {
  email: string;
  role: 'Admin' | 'Patient';
}

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
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
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

  // Login
  protected readonly loggedIn = signal(false);
  protected readonly role = signal<'Admin' | 'Patient' | null>(null);
  protected readonly loginEmail = signal('');
  protected readonly loginPassword = signal('');
  protected readonly loginError = signal('');

  // Admin settings (workflow URL, persisted server-side)
  protected readonly settingsOpen = signal(false);
  protected readonly workflowUrlDraft = signal('');
  protected readonly settingsError = signal('');

  // Hospital picker (loaded from the database)
  protected readonly hospitals = signal<Hospital[]>([]);
  protected readonly hospitalModalOpen = signal(false);
  protected readonly selectedHospitalId = signal<number | null>(null);
  protected readonly selectedHospital = computed(() =>
    this.hospitals().find((h) => h.id === this.selectedHospitalId()) ?? null
  );

  protected readonly processing = signal(false);
  protected readonly processingStep = signal('');
  protected readonly errorMessage = signal('');

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

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    // The OAuth "Connect Get Data" flow opens the workflow URL in a new tab; when it redirects back the
    // Angular app boots from scratch in that tab. Re-check the hb_session cookie (shared across tabs, unlike
    // this component's signals) so an already-logged-in user lands on the dashboard instead of the login screen.
    void this.restoreSession();
  }

  private async restoreSession(): Promise<void> {
    try {
      const response = await firstValueFrom(
        this.http.get<LoginResponse>(`${BACKEND_BASE_URL}/api/session`, { withCredentials: true })
      );
      this.role.set(response.role);
      this.loggedIn.set(true);
    } catch {
      // No valid session cookie — stay on the login screen.
    }
  }

  async login(): Promise<void> {
    this.loginError.set('');

    try {
      const response = await firstValueFrom(
        this.http.post<LoginResponse>(
          `${BACKEND_BASE_URL}/api/login`,
          { email: this.loginEmail(), password: this.loginPassword() },
          { withCredentials: true }
        )
      );

      this.role.set(response.role);
      this.loggedIn.set(true);
    } catch {
      this.loginError.set('Invalid email or password.');
    }
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.post(`${BACKEND_BASE_URL}/api/logout`, {}, { withCredentials: true }));
    this.loggedIn.set(false);
    this.role.set(null);
    this.loginEmail.set('');
    this.loginPassword.set('');
    this.errorMessage.set('');
    this.screen.set('home');
    this.records.set([]);
  }

  async openSettings(): Promise<void> {
    this.settingsError.set('');

    try {
      const current = await firstValueFrom(
        this.http.get<{ workflowUrl: string }>(`${BACKEND_BASE_URL}/api/settings`, { withCredentials: true })
      );
      this.workflowUrlDraft.set(current.workflowUrl);
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
          { workflowUrl: this.workflowUrlDraft() },
          { withCredentials: true }
        )
      );
      this.settingsOpen.set(false);
    } catch {
      this.settingsError.set('Could not save settings.');
    }
  }

  async openConnectFlow(): Promise<void> {
    this.errorMessage.set('');
    this.selectedHospitalId.set(null);

    try {
      const hospitals = await firstValueFrom(
        this.http.get<Hospital[]>(`${BACKEND_BASE_URL}/api/hospitals`, { withCredentials: true })
      );
      this.hospitals.set(hospitals);
      this.hospitalModalOpen.set(true);
    } catch {
      this.errorMessage.set('Could not load the hospital list.');
    }
  }

  selectHospital(id: number): void {
    this.selectedHospitalId.set(id);
  }

  closeHospitalModal(): void {
    this.hospitalModalOpen.set(false);
  }

  async process(): Promise<void> {
    const hospital = this.selectedHospital();
    if (!hospital) {
      return;
    }

    this.hospitalModalOpen.set(false);
    this.processing.set(true);
    this.errorMessage.set('');
    this.processingStep.set(`Opening workflow for ${hospital.name}...`);

    try {
      const settings = await firstValueFrom(
        this.http.get<{ workflowUrl: string }>(`${BACKEND_BASE_URL}/api/workflow-url`, { withCredentials: true })
      );

      if (!settings.workflowUrl) {
        this.errorMessage.set('No workflow URL is configured. Ask an admin to set one.');
        return;
      }

      window.open(settings.workflowUrl, '_blank', 'noopener,noreferrer');
    } catch {
      this.errorMessage.set('Could not load the workflow URL.');
    } finally {
      this.processing.set(false);
    }
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
