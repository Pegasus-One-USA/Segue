import { Component, OnInit, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const BACKEND_BASE_URL = environment.healthAppBase;

// A bare GUID (with or without hyphens) is never a valid FHIRBridge launch-context token — real tokens are Data
// Protection-encrypted, URL-safe base64 (100+ chars, no fixed hyphen pattern). This exists purely to catch the
// easy mistake of pasting a workflow id where a minted launch-context token belongs (see GET
// /api/v1/workflows/{id}/launch-url) — it does not attempt to validate that a non-GUID-shaped value is real.
const GUID_PATTERN = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

/** Matches Demo_TestApp's GET/POST /api/settings response — the single, unified settings surface for every demo
 *  type's FHIRBridge connection points. Only the Admin role can read or write this. */
interface AdminSettings {
  workflowUrl: string;
  patientWorkflowId: string;
  patientDetailWorkflowId: string;
  patientBaseUrl: string;
  patientCsvExportWorkflowId: string;
  patientCsvEmailExportWorkflowId: string;
  standaloneWorkflowId: string;
  standaloneDetailWorkflowId: string;
  standaloneBaseUrl: string;
  providerLaunchContext: string;
}

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
  readonly standaloneWorkflowId = signal('');
  readonly standaloneDetailWorkflowId = signal('');
  readonly standaloneBaseUrl = signal('');
  readonly providerLaunchContext = signal('');

  // Drives a warning under the Launch Context field — this exact mistake (an admin pasting the "Provider InApp"
  // workflow's own id here instead of a real minted token) is what caused a full "invalid or has been tampered
  // with" investigation; see fhirbridge-demo-testapp-workflow-ids memory. GET /api/v1/workflows/{id}/launch-url
  // (SuperAdmin bearer token) mints the real value.
  readonly providerLaunchContextLooksLikeWorkflowId = computed(() =>
    GUID_PATTERN.test(this.providerLaunchContext().trim())
  );

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    void this.loadSettings();
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
      this.standaloneWorkflowId.set(current.standaloneWorkflowId);
      this.standaloneDetailWorkflowId.set(current.standaloneDetailWorkflowId);
      this.standaloneBaseUrl.set(current.standaloneBaseUrl);
      this.providerLaunchContext.set(current.providerLaunchContext);
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
            standaloneWorkflowId: this.standaloneWorkflowId(),
            standaloneDetailWorkflowId: this.standaloneDetailWorkflowId(),
            standaloneBaseUrl: this.standaloneBaseUrl(),
            providerLaunchContext: this.providerLaunchContext(),
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
      this.standaloneWorkflowId.set(result.standaloneWorkflowId);
      this.standaloneDetailWorkflowId.set(result.standaloneDetailWorkflowId);
      this.standaloneBaseUrl.set(result.standaloneBaseUrl);
      this.providerLaunchContext.set(result.providerLaunchContext);
      this.saved.set(true);
    } catch {
      this.saveError.set('Could not save settings.');
    }
  }
}
