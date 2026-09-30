import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const BACKEND_BASE_URL = environment.healthAppBase;

/** Matches ClientCredentialsDemoStepResult (backend/ClientCredentialsDemoEndpoints.cs) exactly. */
interface StepResult {
  requestedUrl: string;
  statusCode: number | null;
  success: boolean;
  responseBody: string | null;
  error: string | null;
}

/** Matches ClientCredentialsDemoResult (backend/ClientCredentialsDemoEndpoints.cs) exactly. */
interface DemoResult {
  tokenStep: StepResult;
  accessToken: string | null;
  runStep: StepResult | null;
}

/**
 * A login-free console for exercising FHIRBridge's OAuth 2.0 Client Credentials Grant (Settings > API Clients)
 * end to end: paste a Client ID/Secret an admin generated there, plus a workflow id, and this backend does the
 * two real calls server-to-server — POST /api/v1/oauth/token, then POST /api/v1/workflows/{id}/run with the
 * returned Bearer token — so a developer can see the exact request/response shapes before wiring their own
 * integration. See ClientCredentialsDemoEndpoints.cs; the Secret is proxied through this backend and never
 * leaves it for the browser to see FHIRBridge directly (no CORS setup needed for this demo either).
 *
 * Reached at a dedicated path (see app.ts's isClientCredentialsConsole), rendered BEFORE the login gate — same
 * pattern as the ApiEndpoint destination test console: a developer tool independent of any HealthApp role.
 */
@Component({
  selector: 'app-client-credentials-console',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './client-credentials-console.html',
  styleUrl: './client-credentials-console.scss',
})
export class ClientCredentialsConsoleComponent {
  protected readonly fhirBridgeBaseUrl = signal(environment.fhirbridgeBase);
  protected readonly workflowId = signal('');
  protected readonly clientId = signal('');
  protected readonly clientSecret = signal('');

  protected readonly running = signal(false);
  protected readonly result = signal<DemoResult | null>(null);
  protected readonly runError = signal('');

  constructor(private readonly http: HttpClient) {}

  get curlTokenSample(): string {
    return `curl -X POST '${this.fhirBridgeBaseUrl()}/api/v1/oauth/token' \\
  -H 'Content-Type: application/json' \\
  -d '{"grant_type":"client_credentials","client_id":"${this.clientId() || '<client id>'}","client_secret":"${this.clientSecret() ? '••••••••' : '<client secret>'}"}'`;
  }

  get curlRunSample(): string {
    return `curl -X POST '${this.fhirBridgeBaseUrl()}/api/v1/workflows/${this.workflowId() || '{workflowId}'}/run' \\
  -H 'Authorization: Bearer <access_token from above>' \\
  -H 'Content-Type: application/json' \\
  -d '{}'`;
  }

  async runTest(): Promise<void> {
    this.runError.set('');
    this.result.set(null);

    if (!this.fhirBridgeBaseUrl().trim() || !this.workflowId().trim() || !this.clientId().trim() || !this.clientSecret().trim()) {
      this.runError.set('Fill in every field first.');
      return;
    }

    this.running.set(true);
    try {
      const result = await firstValueFrom(this.http.post<DemoResult>(
        `${BACKEND_BASE_URL}/api/client-credentials-demo/run`,
        {
          fhirBridgeBaseUrl: this.fhirBridgeBaseUrl().trim(),
          workflowId: this.workflowId().trim(),
          clientId: this.clientId().trim(),
          clientSecret: this.clientSecret().trim(),
        },
      ));
      this.result.set(result);
    } catch {
      this.runError.set('Could not reach the demo backend — is it running on ' + BACKEND_BASE_URL + '?');
    } finally {
      this.running.set(false);
    }
  }

  /** Best-effort pretty-print — falls back to the verbatim string when it isn't parseable JSON. */
  prettyBody(body: string | null): string {
    if (!body) return '';
    try {
      return JSON.stringify(JSON.parse(body), null, 2);
    } catch {
      return body;
    }
  }
}
