import { DatePipe } from '@angular/common';
import { Component, OnInit, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { firstValueFrom } from 'rxjs';
import { PatientDetailsComponent } from './patient-details/patient-details';
import { BackendSystemService, PatientDataSource } from './core/services/backend-system.service';
import { PatientListItem } from './core/models/backend-system.model';

@Component({
  selector: 'app-backend-system',
  standalone: true,
  imports: [DatePipe, MatIconModule, MatProgressSpinnerModule, MatTableModule, PatientDetailsComponent],
  templateUrl: './backend-system.html',
  styleUrl: './backend-system.scss',
})
export class BackendSystemComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly displayedColumns = ['patientId', 'fullName', 'mrn', 'identifier', 'gender', 'birthDate'];

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

  constructor(private readonly backendSystem: BackendSystemService) {}

  ngOnInit(): void {
    void this.loadPatients();
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
}
