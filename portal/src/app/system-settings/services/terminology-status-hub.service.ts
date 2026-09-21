import { Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../../environments/environment';

/** Matches the backend's TerminologyStatusChangedEvent (Application.Services.Terminology) — the payload
 *  TerminologyStatusHub pushes as "TerminologyStatusChanged" each time one code system's sync starts or
 *  reaches a terminal state. */
/** The terminal statuses an import can reach, plus "Running". "Interrupted" is distinct from "Failed" on
 *  purpose: it means the host stopped mid-import (a restart, a crash) rather than the import failing on its
 *  own merits, so it is reported differently and is safe to simply run again. */
export type TerminologyStatus = 'Running' | 'Succeeded' | 'Failed' | 'Interrupted';

export interface TerminologyStatusChangedEvent {
  code: string;
  status: TerminologyStatus;
  occurredAt: string;
  importedConceptCount: number | null;
  version: string | null;
  errorMessage: string | null;
}

/**
 * Live sync status for the Terminology Server table, replacing the per-row 3-second history poll that used
 * to drive it. Modelled on RunStatusHubService: one shared connection, connected lazily on first subscriber
 * rather than at app startup (an unauthenticated user has no session to connect with).
 *
 * Failure is silent by design — a blocked WebSocket/long-poll means subscribers simply never receive events.
 * Since the poll is gone, that is also the only fallback this screen has, which is what `reconnected$` is
 * for: the table does one reconciling fetch on every connect and reconnect, so a sync that started or
 * finished while the socket was down is still reflected rather than leaving a row stuck on "Running".
 *
 * The hub is SuperAdmin-only server-side, matching the screen it backs; a connection from anyone else is
 * rejected at the negotiate step and lands in the same silent-fallback path as any other failure.
 */
@Injectable({ providedIn: 'root' })
export class TerminologyStatusHubService {

  private connection: HubConnection | null = null;
  private readonly events$ = new Subject<TerminologyStatusChangedEvent>();
  private readonly connected$ = new Subject<void>();

  /** Live status changes. Patch the matching row by `code` — never refetch the whole table from this. */
  readonly statusChanged$ = this.events$.asObservable();
  /** Fires after every successful connect/reconnect — see the class remarks on reconciling. */
  readonly reconnected$ = this.connected$.asObservable();

  /** Idempotent — safe to call from every consumer's ngOnInit; later calls are no-ops while connected. */
  ensureConnected(): void {
    if (this.connection) {
      return;
    }

    this.connection = new HubConnectionBuilder()
      .withUrl(`${environment.apiBase}/hubs/terminology-status`, {
        // The access token lives in an HttpOnly cookie the browser attaches itself; withCredentials is what
        // makes that happen cross-origin. Same arrangement as RunStatusHubService.
        withCredentials: true,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('TerminologyStatusChanged', (event: TerminologyStatusChangedEvent) => this.events$.next(event));
    this.connection.onreconnected(() => this.connected$.next());

    this.connection.start()
      .then(() => this.connected$.next())
      .catch(() => {
        // Swallowed by design — see the class remarks.
      });
  }
}
