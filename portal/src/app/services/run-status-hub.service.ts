import { Injectable, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';

/** Matches the backend's RunStatusChangedEvent (Runtime.Application.Workflows) — the payload RunStatusHub
 *  pushes as "RunStatusChanged" whenever a Runtime Plane workflow run starts or reaches a terminal state. */
export interface RunStatusChangedEvent {
  workflowRunId: string;
  workflowDefinitionId: string;
  status: 'Running' | 'Succeeded' | 'Failed';
  occurredAt: string;
  errorMessage: string | null;
  /** The Global Exception Manager's ERR-yyyyMMdd-NNNNNN id for this failure, when one was actually persisted
   *  to ErrorLogs — null if no capture ran or it failed to persist (never a placeholder). */
  errorReferenceId: string | null;
}

/**
 * One shared SignalR connection for the whole app — not one per screen, and never one per row (see the earlier
 * discussion on why per-row connections don't scale). The Dashboard and Workflow List both inject this same
 * root-provided singleton and subscribe to the same `runStatusChanged` stream, each patching only the rows/tiles
 * it owns. Connects lazily on first subscriber (via `ensureConnected()`), not eagerly at app startup — an
 * unauthenticated user (login screen) has no token to connect with yet.
 *
 * Falls back silently: if the hub can't connect at all (corporate proxy blocking WebSockets/long-polling, or the
 * connection eventually giving up reconnecting), subscribers simply never receive an event. The Dashboard has its
 * own setInterval refresh as a baseline for that case; the Workflow List has no REST fallback of its own and
 * relies on this hub (plus its own periodic reload()/manual refresh) to reflect run completion.
 */
@Injectable({ providedIn: 'root' })
export class RunStatusHubService {

  private connection: HubConnection | null = null;
  private readonly events$ = new Subject<RunStatusChangedEvent>();
  /** Emits once per successful (re)connect — including the very first connect and every reconnect after a drop —
   *  so a subscriber can do one reconciling REST fetch to catch whatever happened while disconnected, then trust
   *  the live stream again from that point. */
  private readonly connected$ = new Subject<void>();

  /** Live status-change events. Subscribers should patch just the matching row/tile by id — never treat this as
   *  a signal to refetch everything (that defeats the point of pushing only the delta). */
  readonly runStatusChanged$ = this.events$.asObservable();
  /** Fires after every successful connect/reconnect — see the class remarks above. */
  readonly reconnected$ = this.connected$.asObservable();

  /** Idempotent — safe to call from every consumer's constructor/ngOnInit. Only the first call actually starts a
   *  connection; later calls are no-ops while already connected/connecting. */
  ensureConnected(): void {
    if (this.connection) {
      return;
    }

    const hubUrl = `${environment.apiBase}/hubs/run-status`;

    this.connection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        // HIPAA #7: the access token lives in an HttpOnly cookie now, unreadable by this client — the browser
        // attaches it automatically to the negotiate call and the WebSocket/SSE handshake alike (withCredentials
        // is what makes that happen cross-origin, same as HttpClient's requests). See the matching server-side
        // cookie fallback in FhirBridgeAuthenticationExtensions.ApplyCookieTokenFallback.
        withCredentials: true,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('RunStatusChanged', (event: RunStatusChangedEvent) => this.events$.next(event));
    this.connection.onreconnected(() => this.connected$.next());

    this.connection.start()
      .then(() => this.connected$.next())
      .catch(() => {
        // Swallowed by design — see the class remarks on falling back to each screen's existing polling.
      });
  }

}
