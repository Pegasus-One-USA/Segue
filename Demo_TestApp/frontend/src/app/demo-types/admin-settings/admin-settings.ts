import { Component, OnInit, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const BACKEND_BASE_URL = environment.healthAppBase;

/** Matches Demo_TestApp's GET/POST /api/settings response — the single, unified settings surface for every demo
 *  type's FHIRBridge connection points. Only the Admin role can read or write this. */
interface AdminSettings {
  workflowUrl: string;
  patientWorkflowId: string;
  patientDetailWorkflowId: string;
  patientBaseUrl: string;
  standaloneWorkflowId: string;
  standaloneDetailWorkflowId: string;
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
  readonly standaloneWorkflowId = signal('');
  readonly standaloneDetailWorkflowId = signal('');
  readonly providerLaunchContext = signal('');

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
      this.standaloneWorkflowId.set(current.standaloneWorkflowId);
      this.standaloneDetailWorkflowId.set(current.standaloneDetailWorkflowId);
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
            standaloneWorkflowId: this.standaloneWorkflowId(),
            standaloneDetailWorkflowId: this.standaloneDetailWorkflowId(),
            providerLaunchContext: this.providerLaunchContext(),
          },
          { withCredentials: true }
        )
      );
      this.workflowUrl.set(result.workflowUrl);
      this.patientWorkflowId.set(result.patientWorkflowId);
      this.patientDetailWorkflowId.set(result.patientDetailWorkflowId);
      this.patientBaseUrl.set(result.patientBaseUrl);
      this.standaloneWorkflowId.set(result.standaloneWorkflowId);
      this.standaloneDetailWorkflowId.set(result.standaloneDetailWorkflowId);
      this.providerLaunchContext.set(result.providerLaunchContext);
      this.saved.set(true);
    } catch {
      this.saveError.set('Could not save settings.');
    }
  }
}
