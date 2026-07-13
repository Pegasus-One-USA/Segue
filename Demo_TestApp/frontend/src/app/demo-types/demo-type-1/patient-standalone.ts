import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

const BACKEND_BASE_URL = 'http://localhost:5500';

interface Hospital {
  id: number;
  name: string;
  organizationId: number;
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
  selector: 'app-patient-standalone',
  imports: [FormsModule],
  templateUrl: './patient-standalone.html',
  styleUrl: './patient-standalone.scss'
})
export class PatientStandaloneComponent {
  readonly role = input<'Admin' | 'Patient' | null>(null);
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
