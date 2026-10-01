import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SearchableOption, SearchableSelectComponent } from '../../core/searchable-select/searchable-select.component';

const BACKEND_BASE_URL = environment.healthAppBase;

// Only ever holds Client ID/Secret for THIS dev console, opt-in via the "Remember" checkbox, in this
// browser's own localStorage — never sent anywhere but back into this page's own form fields.
const STORAGE_KEY = 'external-trigger-console.credentials';

interface StoredCredentials {
  fhirBridgeBaseUrl: string;
  clientId: string;
  clientSecret: string;
}

interface WorkflowOption {
  id: string;
  name: string;
  sourceSystemType: string | null;
}

interface ListWorkflowsResponse {
  success: boolean;
  error?: string;
  workflows: WorkflowOption[];
}

interface EhrEndpointOption {
  code: string;
  name: string;
  vendor: string;
}

interface ListEhrEndpointsResponse {
  success: boolean;
  error?: string;
  ehrEndpoints: EhrEndpointOption[];
}

function loadStoredCredentials(): StoredCredentials | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) as StoredCredentials : null;
  } catch {
    // Private browsing / blocked storage — fall back to blank fields, same as never having saved.
    return null;
  }
}

/**
 * Login-free console for FHIRBridge's browser-redirect external-trigger flow
 * (`POST /api/v1/workflows/external/run`) — the counterpart of ClientCredentialsConsoleComponent's
 * server-to-server flow. Unlike that one, the "Trigger Workflow" action here is a REAL HTML
 * `<form method="post">` doing a genuine top-level navigation to FHIRBridge, not a fetch proxied through this
 * app's own backend: that's what makes the Referer FHIRBridge sees this page's real origin, and what
 * exercises the actual redirect-out/redirect-back loop (Return URL allow-list + Referer check) for real. Only
 * the read-only workflow list is proxied through the demo backend (see ExternalTriggerDemoEndpoints) — a
 * fetch straight to FHIRBridge from here would need this page's origin on FHIRBridge's CORS allow-list, which
 * a server-to-server proxy call avoids entirely.
 *
 * Reached at a dedicated path (see app.ts's isExternalTriggerConsole), rendered BEFORE the login gate — same
 * pattern as the other two developer consoles.
 */
@Component({
  selector: 'app-external-trigger-console',
  standalone: true,
  imports: [CommonModule, FormsModule, SearchableSelectComponent],
  templateUrl: './external-trigger-console.html',
  styleUrl: './external-trigger-console.scss',
})
export class ExternalTriggerConsoleComponent implements OnInit {
  protected readonly fhirBridgeBaseUrl = signal(environment.fhirbridgeBase);
  protected readonly clientId = signal('');
  protected readonly clientSecret = signal('');
  protected readonly rememberCredentials = signal(false);
  protected readonly ehrEndpointCode = signal('');
  /** Per-run Group ID / search criteria; blank keeps the workflow's saved values. */
  protected readonly groupId = signal('');
  protected readonly searchCriteria = signal('');
  protected readonly mode = signal<'async' | 'sync'>('async');
  /** Where the run happens: a new window (this console stays put) or this same window (replaced by the run page). */
  protected readonly windowMode = signal<'new' | 'same'>('new');
  /** New window only: close it automatically when finished, instead of returning it to the Return URL. */
  protected readonly closeOnComplete = signal(false);
  protected readonly returnUrl = signal(window.location.origin + window.location.pathname);

  protected readonly workflows = signal<WorkflowOption[]>([]);
  protected readonly selectedWorkflowId = signal('');
  protected readonly listing = signal(false);
  protected readonly listError = signal('');

  protected readonly ehrEndpoints = signal<EhrEndpointOption[]>([]);

  /** The selected workflow's own source vendor (e.g. "Epic", "Athena") — narrows the EHR Endpoint Code
   *  dropdown to that vendor's directory instead of every vendor's endpoints at once. Falls back to the
   *  full unfiltered list when the workflow's source vendor couldn't be resolved (e.g. a Draft with no
   *  source wired up yet), since an empty dropdown would be worse than an unfiltered one there. */
  protected readonly filteredEhrEndpoints = computed(() => {
    const all = this.ehrEndpoints();
    const workflow = this.workflows().find(w => w.id === this.selectedWorkflowId());
    const vendor = workflow?.sourceSystemType;
    if (!vendor) return all;

    const matching = all.filter(e => e.vendor.toLowerCase() === vendor.toLowerCase());
    return matching.length > 0 ? matching : all;
  });

  protected readonly workflowOptions = computed<SearchableOption[]>(() =>
    this.workflows().map(w => ({ value: w.id, label: w.name, hint: w.sourceSystemType ?? undefined })));

  protected readonly ehrEndpointOptions = computed<SearchableOption[]>(() =>
    this.filteredEhrEndpoints().map(e => ({ value: e.code, label: e.name, hint: e.vendor })));

  // Populated from ?status=&workflowRunId=&error= on the return leg of the redirect round trip.
  protected readonly returnedStatus = signal<string | null>(null);
  protected readonly returnedWorkflowRunId = signal<string | null>(null);
  protected readonly returnedError = signal<string | null>(null);

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    const params = new URLSearchParams(window.location.search);
    const status = params.get('status');
    if (status) {
      this.returnedStatus.set(status);
      this.returnedWorkflowRunId.set(params.get('workflowRunId'));
      this.returnedError.set(params.get('error'));
      // Strip so a refresh doesn't replay this as if a new trigger just returned.
      window.history.replaceState(null, '', window.location.pathname);
    }

    const stored = loadStoredCredentials();
    if (stored) {
      this.fhirBridgeBaseUrl.set(stored.fhirBridgeBaseUrl);
      this.clientId.set(stored.clientId);
      this.rememberCredentials.set(true);
    }
  }

  /** Called from the Remember checkbox and after a successful List Workflows — keeps storage in sync with
   *  the checkbox's current state rather than only writing once. */
  onRememberChange(remember: boolean): void {
    this.rememberCredentials.set(remember);
    if (remember) {
      this.saveCredentials();
    } else {
      try { localStorage.removeItem(STORAGE_KEY); } catch { /* ignore */ }
    }
  }

  private saveCredentials(): void {
    if (!this.rememberCredentials()) return;
    try {
      const value: StoredCredentials = {
        fhirBridgeBaseUrl: this.fhirBridgeBaseUrl().trim(),
        clientId: this.clientId().trim(),
        clientSecret: '', // the secret is deliberately never persisted in the browser
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(value));
    } catch {
      // Private browsing / blocked storage — the checkbox stays checked but nothing persists; harmless.
    }
  }

  protected readonly launching = signal(false);
  protected readonly launchError = signal('');

  /** Asks the demo BACKEND to exchange the Client ID/Secret for a single-use launch ticket, then sends the browser
   *  to FHIRBridge's run page with only that ticket. The secret never appears in a form field or URL. */
  async launch(): Promise<void> {
    this.launchError.set('');
    if (!this.selectedWorkflowId() || !this.clientId().trim() || !this.clientSecret().trim()) {
      this.launchError.set('Enter the Client ID and Secret, load workflows and pick one first.');
      return;
    }

    // Must open synchronously inside the click handler, otherwise popup blockers refuse it. It is pointed at the
    // run page once the ticket arrives; a script-opened window is also one this page may close itself.
    const newWindow = this.windowMode() === 'new' ? window.open('', '_blank') : null;
    this.launching.set(true);
    try {
      const result = await firstValueFrom(this.http.post<{ success: boolean; error?: string; runUrl?: string }>(
        `${BACKEND_BASE_URL}/api/external-trigger-demo/launch`,
        {
          fhirBridgeBaseUrl: this.fhirBridgeBaseUrl().trim(),
          clientId: this.clientId().trim(),
          clientSecret: this.clientSecret().trim(),
          workflowId: this.selectedWorkflowId(),
          returnUrl: this.returnUrl().trim(),
          ehrEndpointCode: this.ehrEndpointCode(),
          groupId: this.groupId().trim(),
          searchCriteria: this.searchCriteria().trim(),
          mode: this.mode(),
          windowMode: this.windowMode(),
          closeOnComplete: this.closeOnComplete() ? 'true' : 'false',
        },
      ));

      if (!result.success || !result.runUrl) {
        newWindow?.close();
        this.launchError.set(result.error ?? 'Could not start the workflow.');
        return;
      }

      if (newWindow) {
        newWindow.location.href = result.runUrl;
      } else {
        window.location.href = result.runUrl;
      }
    } catch {
      newWindow?.close();
      this.launchError.set('Could not reach the demo backend — is it running on ' + BACKEND_BASE_URL + '?');
    } finally {
      this.launching.set(false);
    }
  }

  async listWorkflows(): Promise<void> {
    this.listError.set('');
    if (!this.clientId().trim() || !this.clientSecret().trim()) {
      this.listError.set('Enter the Client ID and Secret first.');
      return;
    }

    this.saveCredentials();
    this.listing.set(true);
    try {
      const result = await firstValueFrom(this.http.post<ListWorkflowsResponse>(
        `${BACKEND_BASE_URL}/api/external-trigger-demo/list-workflows`,
        {
          fhirBridgeBaseUrl: this.fhirBridgeBaseUrl().trim(),
          clientId: this.clientId().trim(),
          clientSecret: this.clientSecret().trim(),
          returnUrl: this.returnUrl().trim(),
        },
      ));

      if (!result.success) {
        this.listError.set(result.error ?? 'Could not list workflows.');
        this.workflows.set([]);
        return;
      }

      this.workflows.set(result.workflows);
      if (result.workflows.length > 0) {
        this.selectedWorkflowId.set(result.workflows[0].id);
      }

      // Best-effort — the EHR Endpoint Code field stays optional even if this list fails to load, so a
      // failure here doesn't block triggering (it just falls back to no dropdown options).
      void this.listEhrEndpoints();
    } catch {
      this.listError.set('Could not reach the demo backend — is it running on ' + BACKEND_BASE_URL + '?');
    } finally {
      this.listing.set(false);
    }
  }

  private async listEhrEndpoints(): Promise<void> {
    try {
      const result = await firstValueFrom(this.http.post<ListEhrEndpointsResponse>(
        `${BACKEND_BASE_URL}/api/external-trigger-demo/list-ehr-endpoints`,
        {
          fhirBridgeBaseUrl: this.fhirBridgeBaseUrl().trim(),
          clientId: this.clientId().trim(),
          clientSecret: this.clientSecret().trim(),
          returnUrl: this.returnUrl().trim(),
        },
      ));

      this.ehrEndpoints.set(result.success ? result.ehrEndpoints : []);
    } catch {
      this.ehrEndpoints.set([]);
    }
  }
}
