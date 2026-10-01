import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../../environments/environment';

interface RunStatusResponse {
  status: string;
  errorMessage: string | null;
}

interface TokenStatusResponse {
  hasValidToken: boolean;
}

interface MintResponse {
  launchUrl: string;
  sessionId: string;
}

interface WorkflowRunResponse {
  workflowRunId?: string;
  workflowRun?: { id?: string; status: string; errorMessage: string | null };
}

/** What has to survive the full-page redirect out to the EHR's consent screen and back. */
interface InteractiveRunState {
  workflowId: string;
  flow: 'patient' | 'standalone';
  ehrEndpointId: string | null;
  /** The interactive token-cache key: minted by FHIRBridge, then echoed back as callerId on every later call. */
  sessionId: string | null;
  options: CompletionOptions;
}

/** What to do once the run reaches a terminal state — chosen by the caller, carried through the redirects. */
interface CompletionOptions {
  /** 'new' = the caller opened this in its own window; 'same' = it replaced the caller's page. */
  windowMode: 'new' | 'same';
  /** New window only: close it automatically when finished instead of returning to the return URL. */
  closeOnComplete: boolean;
  /** Signed, expiring token from the API standing in for the validated return URL (never the raw URL). */
  returnToken: string | null;
}

const STATE_KEY = 'external-run.interactive-state';
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
const TERMINAL_STATUSES = new Set(['Succeeded', 'Failed', 'PartialSuccess', 'Cancelled']);
const POLL_INTERVAL_MS = 1500;
/** Long enough for the person to see the outcome before the window closes or navigates away. */
const COMPLETE_DELAY_MS = 2500;

const LAUNCH_ERROR_MESSAGES: Record<string, string> = {
  workflow_failed: 'You signed in successfully, but the workflow could not be run. Check the run history for details.',
  context_mismatch: 'This account is already linked to a different patient/practitioner and cannot be re-authorized with a different one.',
};

const ERROR_MESSAGES: Record<string, string> = {
  invalid_request: 'The trigger request was missing required fields (Client ID, Secret or Workflow).',
  invalid_client: 'Invalid Client ID or Secret, or this client is disabled.',
  workflow_not_found: 'The requested workflow does not exist.',
  invalid_ticket: 'This launch link has expired or was already used. Please start the workflow again from the application.',
  ticket_required: 'This FHIRBridge deployment only accepts launch tickets. The application must obtain one from its backend first.',
  ehr_endpoint_invalid: 'The selected EHR Endpoint cannot be used with this workflow (not found, a different vendor, or a bulk export).',
  license_restricted: 'This workflow cannot be run right now: the license run quota is exhausted or the license does not allow one of its sources or destinations.',
};

function loadState(): InteractiveRunState | null {
  try {
    const raw = localStorage.getItem(STATE_KEY);
    return raw ? JSON.parse(raw) as InteractiveRunState : null;
  } catch {
    return null;
  }
}

function saveState(state: InteractiveRunState): void {
  try { localStorage.setItem(STATE_KEY, JSON.stringify(state)); } catch { /* storage blocked — flow still works within one page life */ }
}

/**
 * Anonymous, pre-login page (see app.routes.ts) that FHIRBridge's own POST /api/v1/workflows/external/run-page
 * redirects to after validating the caller's Client ID/Secret. It never receives that secret — only a workflow id
 * (and, for interactive sources, the flow type and EHR endpoint id). Two paths:
 *
 *  - Non-interactive source: the API already started a background run and passes `runId`; this page polls
 *    GET /workflows/external/run-status/{runId} until it finishes.
 *  - Interactive source (patient / standalone SMART): the documented browser flow — token-status → if there's no
 *    cached token, mint the EHR consent URL and navigate there (the patient authorizes) → the OAuth callback
 *    returns here with `signedIn=1` → run the workflow with callerId = the persisted sessionId.
 */
@Component({
  selector: 'app-external-run',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './external-run.component.html',
  styleUrl: './external-run.component.scss',
})
export class ExternalRunComponent implements OnInit, OnDestroy {
  protected readonly runId = signal<string | null>(null);
  protected readonly startupError = signal<string | null>(null);
  protected readonly status = signal<string | null>(null);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly pollFailed = signal(false);
  /** Shown under the spinner while the interactive flow is between steps (e.g. "Redirecting to sign in…"). */
  protected readonly stepMessage = signal<string | null>(null);

  /** Set when auto-close was requested but the browser refused window.close() and there is no return URL. */
  protected readonly canCloseManually = signal(false);

  /** async = report "execution started" as soon as the run is kicked off, without waiting for it to finish. */
  private asyncMode = true;
  private options: CompletionOptions = { windowMode: 'same', closeOnComplete: false, returnToken: null };
  private pollHandle: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly route: ActivatedRoute, private readonly http: HttpClient) {}

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    this.asyncMode = params.get('mode') !== 'sync';
    this.options = {
      windowMode: params.get('window') === 'new' ? 'new' : 'same',
      closeOnComplete: params.get('close') === '1',
      returnToken: params.get('returnToken'),
    };

    const errorCode = params.get('error');
    if (errorCode) {
      this.startupError.set(ERROR_MESSAGES[errorCode] ?? 'This trigger request could not be processed.');
      return;
    }

    // Returning from the EHR consent screen.
    const launchError = params.get('launchError');
    if (launchError) {
      this.startupError.set(LAUNCH_ERROR_MESSAGES[launchError] ?? 'Authorization could not be completed.');
      return;
    }

    if (params.get('signedIn') === '1' || params.get('workflowRunId')) {
      const state = loadState();
      if (!state) {
        this.startupError.set('Your sign-in completed, but this browser lost the details of the run it belonged to. Please trigger the workflow again.');
        return;
      }
      this.options = state.options;
      this.status.set('Running');
      this.stepMessage.set('Signed in. Running the workflow…');
      void this.runInteractive(state);
      return;
    }

    // Fresh start on an interactive source.
    const workflowId = params.get('workflowId');
    if (workflowId) {
      const flow = params.get('flow') === 'standalone' ? 'standalone' : 'patient';
      const previous = loadState();
      const state: InteractiveRunState = {
        workflowId,
        flow,
        ehrEndpointId: params.get('ehrEndpointId'),
        // Reuse the session for the same workflow so a still-valid cached token is found instead of asking again.
        sessionId: previous?.workflowId === workflowId ? previous.sessionId : null,
        options: this.options,
      };
      this.status.set('Running');
      this.stepMessage.set('Checking authorization…');
      void this.startInteractive(state);
      return;
    }

    // Non-interactive: the API already started the run.
    const runId = params.get('runId');
    if (!runId) {
      this.startupError.set('No workflow run was specified.');
      return;
    }

    this.runId.set(runId);
    if (this.asyncMode) {
      this.status.set('Triggered');
      this.onFinished('Triggered', runId);
      return;
    }

    this.status.set('Running');
    void this.poll(runId);
  }

  ngOnDestroy(): void {
    if (this.pollHandle !== null) clearTimeout(this.pollHandle);
  }

  /** Runs once, when the run reaches a terminal state: close the window, or send the browser to the return URL. */
  private onFinished(status: string, runId: string | null): void {
    const { windowMode, closeOnComplete, returnToken } = this.options;

    const goToReturnUrl = (): boolean => {
      if (!returnToken) return false;
      const outcome = status === 'Succeeded' || status === 'Triggered' ? status : 'Failed';
      const query = new URLSearchParams({ token: returnToken, status: outcome });
      if (runId) query.set('workflowRunId', runId);
      // Through the API, which unpacks the signed token — the portal never handles the raw return URL.
      window.location.href = `${this.api}/workflows/external/return?${query}`;
      return true;
    };

    if (windowMode === 'new' && closeOnComplete) {
      setTimeout(() => {
        window.close();
        // window.close() is silently ignored for windows the browser doesn't consider script-closable.
        setTimeout(() => {
          if (!window.closed && !goToReturnUrl()) this.canCloseManually.set(true);
        }, 400);
      }, COMPLETE_DELAY_MS);
      return;
    }

    if (returnToken) {
      setTimeout(goToReturnUrl, COMPLETE_DELAY_MS);
    }
  }

  private get api(): string {
    return `${environment.apiBase}/api/v1`;
  }

  private async startInteractive(state: InteractiveRunState): Promise<void> {
    try {
      const tokenQuery = state.sessionId ? `?callerId=${encodeURIComponent(state.sessionId)}` : '';
      const token = await firstValueFrom(this.http.get<TokenStatusResponse>(
        `${this.api}/workflows/${state.workflowId}/token-status${tokenQuery}`));

      if (token.hasValidToken) {
        this.stepMessage.set('Already signed in. Running the workflow…');
        await this.runInteractive(state);
        return;
      }

      // No usable token — this is the patient / provider consent screen. The mint endpoint returns the EHR's
      // authorize URL plus the sessionId that becomes the token-cache key; persist it BEFORE navigating away.
      const mintEndpoint = state.flow === 'patient' ? 'public-patient-standalone-url' : 'public-standalone-url';
      const mintParams = new URLSearchParams({
        ehrEndpointId: state.ehrEndpointId ?? EMPTY_GUID,
        callerId: window.location.origin + window.location.pathname,
      });
      if (state.sessionId) mintParams.set('sessionId', state.sessionId);

      const mint = await firstValueFrom(this.http.get<MintResponse>(
        `${this.api}/workflows/${state.workflowId}/${mintEndpoint}?${mintParams}`));

      saveState({ ...state, sessionId: mint.sessionId });
      this.stepMessage.set('Redirecting to sign in and consent…');
      window.location.href = mint.launchUrl;
    } catch {
      this.status.set(null);
      this.startupError.set(
        'Could not start the sign-in for this workflow. It may not be enabled for public launch, or the EHR endpoint is not valid for it.');
    }
  }

  private async runInteractive(state: InteractiveRunState): Promise<void> {
    this.status.set('Running');
    try {
      const result = await firstValueFrom(this.http.post<WorkflowRunResponse>(
        `${this.api}/workflows/${state.workflowId}/run`,
        { patientId: null, patientSearchCriteria: null, callerId: state.sessionId, async: this.asyncMode, triggerType: 'ExternalTrigger' },
      ));
      const startedRunId = result.workflowRun?.id ?? result.workflowRunId ?? null;
      this.runId.set(startedRunId);
      const finalStatus = this.asyncMode && startedRunId ? 'Triggered' : (result.workflowRun?.status ?? 'Failed');
      this.status.set(finalStatus);
      this.errorMessage.set(result.workflowRun?.errorMessage ?? null);
      this.onFinished(finalStatus, startedRunId);
    } catch (err) {
      this.status.set('Failed');
      const body = (err as { error?: { error?: string; message?: string } })?.error;
      this.errorMessage.set(body?.error ?? body?.message ?? 'The workflow run request failed.');
      this.onFinished('Failed', null);
    } finally {
      this.stepMessage.set(null);
    }
  }

  private async poll(runId: string): Promise<void> {
    try {
      const result = await firstValueFrom(this.http.get<RunStatusResponse>(
        `${this.api}/workflows/external/run-status/${runId}`,
      ));
      this.status.set(result.status);
      this.errorMessage.set(result.errorMessage);
      this.pollFailed.set(false);

      if (!TERMINAL_STATUSES.has(result.status)) {
        this.pollHandle = setTimeout(() => void this.poll(runId), POLL_INTERVAL_MS);
      } else {
        this.onFinished(result.status, runId);
      }
    } catch {
      // Transient network hiccup — keep trying rather than giving up on one failed poll.
      this.pollFailed.set(true);
      this.pollHandle = setTimeout(() => void this.poll(runId), POLL_INTERVAL_MS);
    }
  }
}
