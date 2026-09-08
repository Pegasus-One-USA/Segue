import { Component, OnInit, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { isNew11Enabled, setNew11Enabled } from '../new-11/new11-flag';

const BACKEND_BASE_URL = environment.healthAppBase;

/** Matches Demo_TestApp's GET/POST /api/settings response — the single, unified settings surface for every demo
 *  type's FHIRBridge connection points. Only the Admin role can read or write this. */
interface AdminSettings {
  workflowUrl: string;
  patientWorkflowId: string;
  patientDetailWorkflowId: string;
  patientBaseUrl: string;
  patientCsvExportWorkflowId: string;
  patientCsvEmailExportWorkflowId: string;
  athenaPatientWorkflowId: string;
  athenaPatientBaseUrl: string;
  athenaEhrEndpointId: string;
  ecwPatientWorkflowId: string;
  ecwPatientBaseUrl: string;
  ecwEhrEndpointId: string;
  standaloneWorkflowId: string;
  standaloneDetailWorkflowId: string;
  standaloneBaseUrl: string;
  ecwProviderStandaloneListWorkflowId: string;
  ecwProviderStandaloneDetailWorkflowId: string;
  ecwProviderStandaloneBaseUrl: string;
  ecwProviderStandaloneEhrEndpointId: string;
  providerInAppWorkflowId: string;
  ecwProviderInAppWorkflowId: string;
  backendSystemPractitionerImportWorkflowId: string;
}

/** One row of GET/POST /api/v11/workflow-settings — the List + Details workflow URLs for a single role.
 *  Persisted server-side in the <Role>_List_11 / <Role>_Details_11 columns. */
interface Resource11RoleWorkflows {
  role: string;
  listUrl: string;
  detailsUrl: string;
}

// The four non-Admin roles that have a New 11 menu, each with a List and a Details workflow URL.
const NEW11_ROLES = ['Patient', 'ProviderStandalone', 'ProviderInApp', 'BackendSystem'] as const;
const NEW11_ROLE_LABELS: Record<string, string> = {
  Patient: 'Patient',
  ProviderStandalone: 'Provider Standalone',
  ProviderInApp: 'Provider InApp',
  BackendSystem: 'Backend System',
};

@Component({
  selector: 'app-admin-settings',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './admin-settings.html',
  styleUrl: './admin-settings.scss',
})
export class AdminSettingsComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly isLoading = signal(true);
  readonly loadError = signal('');
  readonly saveError = signal('');
  readonly saved = signal(false);

  readonly workflowUrl = signal('');
  readonly patientWorkflowId = signal('');
  readonly patientDetailWorkflowId = signal('');
  readonly patientBaseUrl = signal('');
  readonly patientCsvExportWorkflowId = signal('');
  readonly patientCsvEmailExportWorkflowId = signal('');
  // athenahealth variant of the Patient_Standalone connect/list flow (see launch-standalone-patient.ts's vendor
  // toggle) — deliberately has no detail/CSV-export counterparts; those stay Epic-only.
  readonly athenaPatientWorkflowId = signal('');
  readonly athenaPatientBaseUrl = signal('');
  readonly athenaEhrEndpointId = signal('');
  // eClinicalWorks (eCW) variant of the same connect/list flow — same shape as the athenahealth fields above.
  readonly ecwPatientWorkflowId = signal('');
  readonly ecwPatientBaseUrl = signal('');
  readonly ecwEhrEndpointId = signal('');
  readonly standaloneWorkflowId = signal('');
  readonly standaloneDetailWorkflowId = signal('');
  readonly standaloneBaseUrl = signal('');
  // eClinicalWorks (eCW) variant of the Provider_Standalone list/detail flow — the vendor toggle on the
  // launch-standalone-provider screen. Its EhrEndpoint id is a SEPARATE, Epic-type row (not the patient section's
  // MyChart-type ecwEhrEndpointId), since provider-standalone's launch requires an Epic-type EhrEndpoint.
  readonly ecwProviderStandaloneListWorkflowId = signal('');
  readonly ecwProviderStandaloneDetailWorkflowId = signal('');
  readonly ecwProviderStandaloneBaseUrl = signal('');
  readonly ecwProviderStandaloneEhrEndpointId = signal('');
  readonly providerInAppWorkflowId = signal('');
  // Separate eCW (eClinicalWorks) Provider EMR EHR-launch workflow id — the mint endpoint auto-selects this over
  // providerInAppWorkflowId when the launching EHR's iss is an eCW practice (host *.ecwcloud.com).
  readonly ecwProviderInAppWorkflowId = signal('');
  readonly backendSystemPractitionerImportWorkflowId = signal('');

  // Feature flag (Default tab): whether the "New 11" tab shows up at all in every role's shell. Backed by a cookie
  // (not the /api/settings row) so every role's page — same origin, different route, no shared component tree —
  // can read it synchronously at construction without an extra round trip. See new11-flag.ts.
  readonly new11Enabled = signal(isNew11Enabled());

  // Default | New 11 section switch (the workflow config for each surface lives on its own tab).
  readonly section = signal<'default' | 'new11'>('default');

  // New 11 per-role workflow settings.
  readonly new11Roles = NEW11_ROLES;
  readonly roleLabels = NEW11_ROLE_LABELS;
  readonly new11SelectedRole = signal<string>('Patient');
  // Per-role List + Details workflow URLs.
  readonly new11 = signal<Record<string, { list: string; details: string }>>({
    Patient: { list: '', details: '' },
    ProviderStandalone: { list: '', details: '' },
    ProviderInApp: { list: '', details: '' },
    BackendSystem: { list: '', details: '' },
  });
  readonly new11Loaded = signal(false);
  readonly new11Loading = signal(false);
  readonly new11LoadError = signal('');
  readonly new11SaveError = signal('');
  readonly new11Saved = signal(false);

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    void this.loadSettings();
  }

  toggleNew11Enabled(enabled: boolean): void {
    this.new11Enabled.set(enabled);
    setNew11Enabled(enabled);
  }

  selectSection(section: 'default' | 'new11'): void {
    this.section.set(section);
    if (section === 'new11' && !this.new11Loaded()) {
      void this.loadNew11Settings();
    }
  }

  selectNew11Role(role: string): void {
    this.new11SelectedRole.set(role);
    this.new11Saved.set(false);
  }

  setNew11(role: string, field: 'list' | 'details', value: string): void {
    this.new11.update((map) => ({ ...map, [role]: { ...map[role], [field]: value } }));
    this.new11Saved.set(false);
  }

  private applyNew11Rows(rows: Resource11RoleWorkflows[] | null): void {
    const map = { ...this.new11() };
    for (const row of rows ?? []) {
      map[row.role] = { list: row.listUrl ?? '', details: row.detailsUrl ?? '' };
    }
    this.new11.set(map);
  }

  private async loadNew11Settings(): Promise<void> {
    this.new11Loading.set(true);
    this.new11LoadError.set('');

    try {
      const rows = await firstValueFrom(
        this.http.get<Resource11RoleWorkflows[]>(`${BACKEND_BASE_URL}/api/v11/workflow-settings`, { withCredentials: true })
      );
      this.applyNew11Rows(rows);
      this.new11Loaded.set(true);
    } catch {
      this.new11LoadError.set('Could not load New 11 workflow settings.');
    } finally {
      this.new11Loading.set(false);
    }
  }

  async saveNew11Settings(): Promise<void> {
    this.new11SaveError.set('');
    this.new11Saved.set(false);

    const current = this.new11();
    const payload: Resource11RoleWorkflows[] = this.new11Roles.map((role) => ({
      role,
      listUrl: current[role]?.list ?? '',
      detailsUrl: current[role]?.details ?? '',
    }));

    try {
      const rows = await firstValueFrom(
        this.http.post<Resource11RoleWorkflows[]>(`${BACKEND_BASE_URL}/api/v11/workflow-settings`, payload, { withCredentials: true })
      );
      this.applyNew11Rows(rows);
      this.new11Saved.set(true);
    } catch {
      this.new11SaveError.set('Could not save New 11 workflow settings.');
    }
  }

  private async loadSettings(): Promise<void> {
    this.isLoading.set(true);
    this.loadError.set('');

    try {
      const current = await firstValueFrom(
        this.http.get<AdminSettings>(`${BACKEND_BASE_URL}/api/settings`, { withCredentials: true })
      );
      this.workflowUrl.set(current.workflowUrl);
      this.patientWorkflowId.set(current.patientWorkflowId);
      this.patientDetailWorkflowId.set(current.patientDetailWorkflowId);
      this.patientBaseUrl.set(current.patientBaseUrl);
      this.patientCsvExportWorkflowId.set(current.patientCsvExportWorkflowId);
      this.patientCsvEmailExportWorkflowId.set(current.patientCsvEmailExportWorkflowId);
      this.athenaPatientWorkflowId.set(current.athenaPatientWorkflowId);
      this.athenaPatientBaseUrl.set(current.athenaPatientBaseUrl);
      this.athenaEhrEndpointId.set(current.athenaEhrEndpointId);
      this.ecwPatientWorkflowId.set(current.ecwPatientWorkflowId);
      this.ecwPatientBaseUrl.set(current.ecwPatientBaseUrl);
      this.ecwEhrEndpointId.set(current.ecwEhrEndpointId);
      this.standaloneWorkflowId.set(current.standaloneWorkflowId);
      this.standaloneDetailWorkflowId.set(current.standaloneDetailWorkflowId);
      this.standaloneBaseUrl.set(current.standaloneBaseUrl);
      this.ecwProviderStandaloneListWorkflowId.set(current.ecwProviderStandaloneListWorkflowId);
      this.ecwProviderStandaloneDetailWorkflowId.set(current.ecwProviderStandaloneDetailWorkflowId);
      this.ecwProviderStandaloneBaseUrl.set(current.ecwProviderStandaloneBaseUrl);
      this.ecwProviderStandaloneEhrEndpointId.set(current.ecwProviderStandaloneEhrEndpointId);
      this.providerInAppWorkflowId.set(current.providerInAppWorkflowId);
      this.ecwProviderInAppWorkflowId.set(current.ecwProviderInAppWorkflowId);
      this.backendSystemPractitionerImportWorkflowId.set(current.backendSystemPractitionerImportWorkflowId);
    } catch {
      this.loadError.set('Could not load settings.');
    } finally {
      this.isLoading.set(false);
    }
  }

  async saveSettings(): Promise<void> {
    this.saveError.set('');
    this.saved.set(false);

    try {
      const result = await firstValueFrom(
        this.http.post<AdminSettings>(
          `${BACKEND_BASE_URL}/api/settings`,
          {
            workflowUrl: this.workflowUrl(),
            patientWorkflowId: this.patientWorkflowId(),
            patientDetailWorkflowId: this.patientDetailWorkflowId(),
            patientBaseUrl: this.patientBaseUrl(),
            patientCsvExportWorkflowId: this.patientCsvExportWorkflowId(),
            patientCsvEmailExportWorkflowId: this.patientCsvEmailExportWorkflowId(),
            athenaPatientWorkflowId: this.athenaPatientWorkflowId(),
            athenaPatientBaseUrl: this.athenaPatientBaseUrl(),
            athenaEhrEndpointId: this.athenaEhrEndpointId(),
            ecwPatientWorkflowId: this.ecwPatientWorkflowId(),
            ecwPatientBaseUrl: this.ecwPatientBaseUrl(),
            ecwEhrEndpointId: this.ecwEhrEndpointId(),
            standaloneWorkflowId: this.standaloneWorkflowId(),
            standaloneDetailWorkflowId: this.standaloneDetailWorkflowId(),
            standaloneBaseUrl: this.standaloneBaseUrl(),
            ecwProviderStandaloneListWorkflowId: this.ecwProviderStandaloneListWorkflowId(),
            ecwProviderStandaloneDetailWorkflowId: this.ecwProviderStandaloneDetailWorkflowId(),
            ecwProviderStandaloneBaseUrl: this.ecwProviderStandaloneBaseUrl(),
            ecwProviderStandaloneEhrEndpointId: this.ecwProviderStandaloneEhrEndpointId(),
            providerInAppWorkflowId: this.providerInAppWorkflowId(),
            ecwProviderInAppWorkflowId: this.ecwProviderInAppWorkflowId(),
            backendSystemPractitionerImportWorkflowId: this.backendSystemPractitionerImportWorkflowId(),
          },
          { withCredentials: true }
        )
      );
      this.workflowUrl.set(result.workflowUrl);
      this.patientWorkflowId.set(result.patientWorkflowId);
      this.patientDetailWorkflowId.set(result.patientDetailWorkflowId);
      this.patientBaseUrl.set(result.patientBaseUrl);
      this.patientCsvExportWorkflowId.set(result.patientCsvExportWorkflowId);
      this.patientCsvEmailExportWorkflowId.set(result.patientCsvEmailExportWorkflowId);
      this.athenaPatientWorkflowId.set(result.athenaPatientWorkflowId);
      this.athenaPatientBaseUrl.set(result.athenaPatientBaseUrl);
      this.athenaEhrEndpointId.set(result.athenaEhrEndpointId);
      this.ecwPatientWorkflowId.set(result.ecwPatientWorkflowId);
      this.ecwPatientBaseUrl.set(result.ecwPatientBaseUrl);
      this.ecwEhrEndpointId.set(result.ecwEhrEndpointId);
      this.standaloneWorkflowId.set(result.standaloneWorkflowId);
      this.standaloneDetailWorkflowId.set(result.standaloneDetailWorkflowId);
      this.standaloneBaseUrl.set(result.standaloneBaseUrl);
      this.ecwProviderStandaloneListWorkflowId.set(result.ecwProviderStandaloneListWorkflowId);
      this.ecwProviderStandaloneDetailWorkflowId.set(result.ecwProviderStandaloneDetailWorkflowId);
      this.ecwProviderStandaloneBaseUrl.set(result.ecwProviderStandaloneBaseUrl);
      this.ecwProviderStandaloneEhrEndpointId.set(result.ecwProviderStandaloneEhrEndpointId);
      this.providerInAppWorkflowId.set(result.providerInAppWorkflowId);
      this.ecwProviderInAppWorkflowId.set(result.ecwProviderInAppWorkflowId);
      this.backendSystemPractitionerImportWorkflowId.set(result.backendSystemPractitionerImportWorkflowId);
      this.saved.set(true);
    } catch {
      this.saveError.set('Could not save settings.');
    }
  }
}
