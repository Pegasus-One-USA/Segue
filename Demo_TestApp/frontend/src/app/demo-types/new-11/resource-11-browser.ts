import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import {
  PATIENT_DETAIL_RESOURCES,
  PATIENT_LIST_COLUMNS,
  PATIENT_RESOURCE,
  PRACTITIONER_RESOURCE,
  Resource11Column,
  Resource11MenuItem,
} from './core/config/resource-11-menu.config';
import { MissingPractitioner, Resource11Service } from './core/services/resource-11.service';

type Mode = 'patients' | 'practitioners';

// Shared "New 11" browser. Two top-level views over the curated _11 tables:
//  • Patients   — a Patient List; clicking a row opens that patient's per-resource tabs (Encounter, Observation, …).
//  • Practitioners — the global Practitioner_11 list plus an "Import Practitioners" button that pulls the ids
//    referenced across the other tables but not yet present here, via the role's configured New 11 workflow.
// The same component serves every non-Admin role's New 11 menu; the per-role wrappers just embed it.
@Component({
  selector: 'app-resource-11-browser',
  standalone: true,
  imports: [MatIconModule, MatProgressSpinnerModule],
  providers: [DatePipe],
  templateUrl: './resource-11-browser.html',
  styleUrl: './resource-11-browser.scss',
})
export class Resource11BrowserComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();
  // Set only by ProviderStandaloneNew11Component (see readProviderStandaloneSessionId) — forwarded to
  // importMissingPractitioners' /api/v11/practitioners/import call so a Provider Standalone source's
  // CallerId-keyed token cache is found. Other roles (Patient, Backend Services, Provider In-App) leave this
  // unset; Backend Services in particular needs no CallerId at all (its FhirSourceConfiguration.ApplicationType
  // is never Standalone/Patient, so BuildStoreKey ignores CallerId regardless).
  readonly callerId = input<string | null>(null);
  // Set only by ProviderStandaloneNew11Component (see PROVIDER_STANDALONE_PRACTITIONER_IDS) — an explicit,
  // curated id list that overrides the "missing ids" auto-discovery for importMissingPractitioners below. Other
  // roles leave this unset and keep the original auto-discovery behavior.
  readonly practitionerIds = input<string[] | null>(null);

  readonly mode = signal<Mode>('patients');

  // --- Patients: list -> per-patient resource tabs ---
  readonly patientListColumns = PATIENT_LIST_COLUMNS;
  readonly detailResources = PATIENT_DETAIL_RESOURCES;

  readonly patients = signal<Record<string, unknown>[]>([]);
  readonly patientsLoading = signal(true);
  readonly patientsError = signal<string | null>(null);

  readonly selectedPatient = signal<Record<string, unknown> | null>(null);
  readonly detailResource = signal<Resource11MenuItem>(PATIENT_DETAIL_RESOURCES[0]);
  readonly detailRows = signal<Record<string, unknown>[]>([]);
  readonly detailLoading = signal(false);
  readonly detailError = signal<string | null>(null);

  // --- Practitioners: global list + import ---
  readonly practitionerColumns = PRACTITIONER_RESOURCE.columns;
  readonly practitioners = signal<Record<string, unknown>[]>([]);
  readonly practitionersLoading = signal(false);
  readonly practitionersError = signal<string | null>(null);
  private readonly practitionersLoaded = signal(false);

  readonly missingPractitioners = signal<MissingPractitioner[]>([]);
  // True whenever there's something to import — either the usual "missing ids" auto-discovery found rows, or an
  // explicit curated id list (practitionerIds input) was supplied, which imports unconditionally regardless of
  // what's already present locally.
  readonly canImportPractitioners = computed(
    () => this.missingPractitioners().length > 0 || (this.practitionerIds()?.length ?? 0) > 0,
  );
  readonly importing = signal(false);
  readonly importMessage = signal<string | null>(null);
  readonly importError = signal<string | null>(null);

  private readonly resource11 = inject(Resource11Service);
  private readonly datePipe = inject(DatePipe);

  ngOnInit(): void {
    void this.loadPatients();
  }

  selectMode(mode: Mode): void {
    if (this.mode() === mode) {
      return;
    }
    this.mode.set(mode);
    if (mode === 'practitioners' && !this.practitionersLoaded()) {
      void this.loadPractitioners();
    }
  }

  // ---- Patients ----
  private async loadPatients(): Promise<void> {
    this.patientsLoading.set(true);
    this.patientsError.set(null);
    try {
      const rows = await firstValueFrom(this.resource11.getRows(PATIENT_RESOURCE));
      this.patients.set(rows ?? []);
    } catch {
      this.patientsError.set('Could not load patients. Make sure the _11 tables exist and are populated.');
      this.patients.set([]);
    } finally {
      this.patientsLoading.set(false);
    }
  }

  openPatient(patient: Record<string, unknown>): void {
    this.selectedPatient.set(patient);
    this.detailResource.set(PATIENT_DETAIL_RESOURCES[0]);
    void this.loadDetail();
  }

  backToList(): void {
    this.selectedPatient.set(null);
    this.detailRows.set([]);
  }

  selectDetailResource(item: Resource11MenuItem): void {
    if (this.detailResource().key === item.key) {
      return;
    }
    this.detailResource.set(item);
    void this.loadDetail();
  }

  patientTitle(): string {
    const p = this.selectedPatient();
    return p ? String(p['fullName'] ?? p['patientId'] ?? 'Patient') : '';
  }

  private async loadDetail(): Promise<void> {
    const patient = this.selectedPatient();
    if (!patient) {
      return;
    }
    const patientId = String(patient['patientId'] ?? '');
    this.detailLoading.set(true);
    this.detailError.set(null);
    try {
      const rows = await firstValueFrom(this.resource11.getPatientResourceRows(patientId, this.detailResource()));
      this.detailRows.set(rows ?? []);
    } catch {
      this.detailError.set(`Could not load ${this.detailResource().label} for this patient.`);
      this.detailRows.set([]);
    } finally {
      this.detailLoading.set(false);
    }
  }

  // ---- Practitioners ----
  private async loadPractitioners(): Promise<void> {
    this.practitionersLoading.set(true);
    this.practitionersError.set(null);
    try {
      const rows = await firstValueFrom(this.resource11.getRows(PRACTITIONER_RESOURCE));
      this.practitioners.set(rows ?? []);
      this.practitionersLoaded.set(true);
    } catch {
      this.practitionersError.set('Could not load practitioners. Make sure the _11 tables exist and are populated.');
      this.practitioners.set([]);
    } finally {
      this.practitionersLoading.set(false);
    }
    await this.loadMissing();
  }

  private async loadMissing(): Promise<void> {
    try {
      const missing = await firstValueFrom(this.resource11.getMissingPractitioners());
      this.missingPractitioners.set(missing ?? []);
    } catch {
      // Best-effort — if the reference tables can't be read, just don't offer an import.
      this.missingPractitioners.set([]);
    }
  }

  async importMissingPractitioners(): Promise<void> {
    if (this.importing() || !this.canImportPractitioners()) {
      return;
    }
    this.importing.set(true);
    this.importMessage.set(null);
    this.importError.set(null);
    try {
      const result = await firstValueFrom(
        this.resource11.importPractitioners(this.callerId() ?? undefined, this.practitionerIds() ?? undefined),
      );
      if (result.status === 'Succeeded') {
        await this.loadPractitioners(); // refresh table + recompute what's still missing
        this.importMessage.set(result.message ?? `Imported ${result.imported ?? 0} practitioner(s).`);
      } else {
        this.importError.set(result.errorMessage ?? 'Import failed.');
      }
    } catch {
      this.importError.set('Import request failed. Please try again.');
    } finally {
      this.importing.set(false);
    }
  }

  // Turns a raw cell value into display text: '-' for missing, Yes/No for booleans, localized dates for date/
  // datetime columns, and the value itself otherwise.
  formatCell(row: Record<string, unknown>, col: Resource11Column): string {
    const value = row[col.key];
    if (value === null || value === undefined || value === '') {
      return '-';
    }

    switch (col.type) {
      case 'date':
        return this.datePipe.transform(value as string, 'mediumDate') ?? String(value);
      case 'datetime':
        return this.datePipe.transform(value as string, 'medium') ?? String(value);
      case 'boolean':
        return value ? 'Yes' : 'No';
      default:
        return String(value);
    }
  }
}
