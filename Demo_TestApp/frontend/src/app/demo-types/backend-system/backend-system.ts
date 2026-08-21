import { DatePipe } from '@angular/common';
import { Component, OnInit, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import { PatientDetailsComponent } from './patient-details/patient-details';
import { BackendSystemService, PatientDataSource } from './core/services/backend-system.service';
import { Practitioner, PatientListItem, ReferencedPractitioner } from './core/models/backend-system.model';

type BackendSystemView = 'patients' | 'practitioners';

// Shown pre-filled in the "Practitioner IDs" input when the Practitioners view has nothing to seed it with yet —
// no rows in the Practitioner table and no ids referenced across the clinical tables — so the import flow always
// has a usable starting point on a fresh/empty database rather than an empty box.
const FALLBACK_PRACTITIONER_IDS = [
  'eM5CWtq15N0WJeuCet5bJlQ3',
  'eoScyX3bs1SY.2lMoHCGcVw3',
  'exAJI2s53wwSDac-DZlyA.g3',
  'eOyFJ.PiGBcbhr3T1oyJZ1A3',
  'elpRiy0AYgjhdjAhTBJ3Aiw3',
  'evNp-KhYwOOqAZn1pZ2enuA3',
  'e2qocqJm-DdjHLS0Not4qjA3',
  'e9s-IdXQOUVywHOVoisd6xQ3',
  'eHP1iZcoohQAKIcKyP6CDvA3',
  'eJukOp-92bJmlEhVqi9fEvA3',
  'eU2bhJHhLPs8.VF8LzIGbIw3',
  'eUXiD4IyBLJFoR2VHOjfTdg3',
  'eW.s0G2HJ4Csj548x4hZ37A3',
  'emSjrED0EBZP2lU7eSyPE6w3',
  'exfo6E4EXjWsnhA1OGVElgw3',
];

interface PractitionerColumn {
  key: keyof Practitioner;
  label: string;
}

@Component({
  selector: 'app-backend-system',
  standalone: true,
  imports: [DatePipe, MatIconModule, MatProgressSpinnerModule, PatientDetailsComponent],
  templateUrl: './backend-system.html',
  styleUrl: './backend-system.scss',
})
export class BackendSystemComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly isLoading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly patients = signal<PatientListItem[]>([]);

  // Data Source radio group — switching it re-fetches the list immediately (see selectDataSource below).
  // Defaults to 'sql' so existing behavior is unchanged until an admin/tester deliberately picks another source.
  readonly dataSource = signal<PatientDataSource>('sql');
  readonly dataSourceOptions: { value: PatientDataSource; label: string }[] = [
    { value: 'sql', label: 'SQL' },
    { value: 'mysql', label: 'MySQL' },
    { value: 'nosql', label: 'NoSQL' },
  ];

  // Null (not just unset) once a patient is selected, so the template can tell "still on the list" apart from
  // "navigated to details" with a single check, matching the rest of this app's signal-driven view swapping
  // (see app.ts/app.html) rather than introducing a second, real Router route for this one hop.
  readonly selectedPatient = signal<PatientListItem | null>(null);

  // Top-level menu on the Default screen: the existing Patient List, or the new global Practitioners view.
  readonly view = signal<BackendSystemView>('patients');

  // Practitioners view — global list from the Practitioner table, plus the referenced-id discovery + import flow.
  readonly practitionerColumns: PractitionerColumn[] = [
    { key: 'practitionerId', label: 'Practitioner ID' },
    { key: 'fullName', label: 'Name' },
    { key: 'npi', label: 'NPI' },
    { key: 'identifier', label: 'Identifier' },
    { key: 'gender', label: 'Gender' },
    { key: 'qualification', label: 'Qualification' },
    { key: 'phone', label: 'Phone' },
    { key: 'email', label: 'Email' },
  ];
  readonly practitioners = signal<Practitioner[]>([]);
  readonly practitionersLoading = signal(false);
  readonly practitionersError = signal<string | null>(null);
  private practitionersLoaded = false;

  // Practitioner ids referenced across the Default tables (shown as chips; all unique, imported ones flagged).
  readonly referenced = signal<ReferencedPractitioner[]>([]);
  // The editable, comma-separated ids passed to the import workflow — pre-filled from `referenced` on first load.
  readonly practitionerIdsInput = signal('');

  readonly importing = signal(false);
  readonly importMessage = signal<string | null>(null);
  readonly importError = signal<string | null>(null);

  // "Clear Data" — wipes every table the Default screens read from (see clearData() below).
  readonly clearingData = signal(false);
  readonly clearDataMessage = signal<string | null>(null);
  readonly clearDataError = signal<string | null>(null);

  constructor(private readonly backendSystem: BackendSystemService) {}

  ngOnInit(): void {
    void this.loadPatients();
  }

  selectView(view: BackendSystemView): void {
    if (this.view() === view) {
      return;
    }
    this.view.set(view);
    if (view === 'practitioners' && !this.practitionersLoaded) {
      void this.loadPractitionersView();
    }
  }

  private async loadPractitionersView(): Promise<void> {
    await Promise.all([this.loadPractitioners(), this.loadReferenced()]);
    this.practitionersLoaded = true;

    // Nothing in the Practitioner table and no ids referenced across the clinical tables — fall back to a known
    // set of practitioner ids so "Import Practitioner" isn't left pointing at an empty box.
    if (
      this.practitioners().length === 0 &&
      this.referenced().length === 0 &&
      !this.practitionerIdsInput().trim()
    ) {
      this.practitionerIdsInput.set(FALLBACK_PRACTITIONER_IDS.join(', '));
    }
  }

  private async loadPractitioners(): Promise<void> {
    this.practitionersLoading.set(true);
    this.practitionersError.set(null);
    try {
      const rows = await firstValueFrom(this.backendSystem.getPractitioners());
      this.practitioners.set(rows ?? []);
    } catch {
      this.practitionersError.set('Could not load practitioners.');
      this.practitioners.set([]);
    } finally {
      this.practitionersLoading.set(false);
    }
  }

  private async loadReferenced(): Promise<void> {
    try {
      const refs = await firstValueFrom(this.backendSystem.getReferencedPractitionerIds());
      this.referenced.set(refs ?? []);
      // Only pre-fill the input on first load — don't clobber whatever the user has since typed/edited.
      if (!this.practitionerIdsInput().trim()) {
        this.practitionerIdsInput.set((refs ?? []).map((r) => r.practitionerId).join(', '));
      }
    } catch {
      this.referenced.set([]);
    }
  }

  async importPractitioners(): Promise<void> {
    if (this.importing()) {
      return;
    }

    const ids = this.parseIds(this.practitionerIdsInput());
    if (ids.length === 0) {
      this.importError.set('Enter at least one practitioner ID to import.');
      this.importMessage.set(null);
      return;
    }

    this.importing.set(true);
    this.importMessage.set(null);
    this.importError.set(null);
    try {
      const result = await firstValueFrom(this.backendSystem.importPractitioners(ids));
      if (result.status === 'Succeeded') {
        this.importMessage.set(result.message ?? `Imported ${result.imported ?? 0} practitioner(s).`);
        // Refresh the table and the referenced chips (imported flags move) after a successful run.
        await Promise.all([this.loadPractitioners(), this.loadReferenced()]);
      } else {
        this.importError.set(result.errorMessage ?? 'Import failed.');
      }
    } catch {
      this.importError.set('Import request failed. Please try again.');
    } finally {
      this.importing.set(false);
    }
  }

  practitionerCell(row: Practitioner, key: keyof Practitioner): string {
    const value = row[key];
    return value === null || value === undefined || value === '' ? '-' : String(value);
  }

  // Split on commas/whitespace, trim, drop blanks, de-duplicate — tolerant of however the user pastes/edits the ids.
  private parseIds(text: string): string[] {
    return Array.from(new Set(text.split(/[\s,]+/).map((s) => s.trim()).filter(Boolean)));
  }

  private async loadPatients(): Promise<void> {
    this.isLoading.set(true);
    this.loadError.set(null);

    try {
      const patients = await firstValueFrom(this.backendSystem.getPatients(this.dataSource()));
      this.patients.set(patients ?? []);
    } catch {
      this.loadError.set('Could not load the patient list.');
      this.patients.set([]);
    } finally {
      this.isLoading.set(false);
    }
  }

  selectDataSource(source: PatientDataSource): void {
    if (this.dataSource() === source) {
      return;
    }
    this.dataSource.set(source);
    void this.loadPatients();
  }

  openPatient(patient: PatientListItem): void {
    this.selectedPatient.set(patient);
  }

  backToList(): void {
    this.selectedPatient.set(null);
  }

  // Wipes Patient_NewMapped, Practitioner, and every clinical resource table the Default screens read from, then
  // refreshes whichever view is currently on screen so the now-empty state shows immediately.
  async clearData(): Promise<void> {
    if (this.clearingData()) {
      return;
    }
    if (!confirm('This will permanently delete every patient, practitioner, and clinical record from the Default tables. Continue?')) {
      return;
    }

    this.clearingData.set(true);
    this.clearDataMessage.set(null);
    this.clearDataError.set(null);
    try {
      await firstValueFrom(this.backendSystem.clearData());
      this.clearDataMessage.set('All Default backend-system data has been cleared.');
      this.selectedPatient.set(null);
      if (this.view() === 'practitioners') {
        this.practitionersLoaded = false;
        await this.loadPractitionersView();
      } else {
        await this.loadPatients();
      }
    } catch {
      this.clearDataError.set('Could not clear the data. Please try again.');
    } finally {
      this.clearingData.set(false);
    }
  }
}
