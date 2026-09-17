import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import {
  HapiTerminologyConfiguration,
  HapiTerminologyConfigurationService,
  HapiTerminologyVersionCheckResult,
} from '../../services/hapi-terminology-configuration.service';
import { TerminologyStatusHubService, TerminologyStatus } from '../../services/terminology-status-hub.service';
import { HapiTerminologyEditDialogComponent } from '../../dialogs/hapi-terminology-edit-dialog/hapi-terminology-edit-dialog.component';
import { HapiTerminologyHistoryDialogComponent } from '../../dialogs/hapi-terminology-history-dialog/hapi-terminology-history-dialog.component';
import { HapiTerminologyCodesDialogComponent, HapiTerminologyCodesDialogData } from '../../dialogs/hapi-terminology-codes-dialog/hapi-terminology-codes-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';

/**
 * One row per HAPI-terminology-server sync system (CVX/DCM/HCPCS/ICD-10-CM/ICD-10-PCS/ICD-11/ICPC-3/
 * LOINC/MeSH/NDC/RxNorm/SNOMED CT/UCUM), replacing the old flat "Terminology:*Hapi:*" settings rows.
 * Rendered on the same General Settings page, above the generic settings table.
 *
 * Three things about how this screen loads, in order:
 *
 *  1. The 13 rows are fetched and rendered first — they are the settings the user came here to read, and
 *     nothing below should hold them up.
 *  2. Each system's version check is then fired INDEPENDENTLY (13 parallel calls, not the one batched
 *     scan-all), so a row's Version cell fills in as soon as its own external source answers instead of
 *     every row waiting on the slowest.
 *  3. Any system the scan finds out of date is synced automatically.
 *
 * Sync status is pushed over SignalR (TerminologyStatusHubService) rather than polled. The previous
 * implementation re-fetched each running system's history every 3 seconds; that only ever saw syncs THIS
 * tab started, missed scheduled ones entirely, and drove the app-wide loader on every tick.
 */
@Component({
  selector: 'app-hapi-terminology-table',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatIconModule, MatMenuModule, MatButtonModule, MatProgressSpinnerModule],
  templateUrl: './hapi-terminology-table.component.html',
  styleUrls: ['./hapi-terminology-table.component.scss'],
})
export class HapiTerminologyTableComponent implements OnInit {
  private readonly svc = inject(HapiTerminologyConfigurationService);
  private readonly hub = inject(TerminologyStatusHubService);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  /** Bound to the parent page's shared search box, so one search filters both General Settings
   *  groups and this table's rows instead of needing a second search field. */
  readonly searchTerm = input('');

  readonly loading = signal(true);
  readonly configs = signal<HapiTerminologyConfiguration[]>([]);

  /** Placeholder rows for the loading skeleton — 13, the fixed number of systems this table always shows,
   *  so the card reserves its real height and the page below it does not jump when the data lands. */
  protected readonly skeletonRows = Array.from({ length: 13 }, (_, i) => i);

  readonly filteredConfigs = computed(() => {
    const term = this.searchTerm().trim().toLowerCase();
    if (!term) return this.configs();
    return this.configs().filter(c => c.displayName.toLowerCase().includes(term) || c.code.toLowerCase().includes(term));
  });

  readonly runningCodes = signal<ReadonlySet<string>>(new Set());
  readonly scanning = signal(false);
  /** Systems whose own version check is still outstanding — per row, since they run independently. */
  readonly scanningCodes = signal<ReadonlySet<string>>(new Set());
  readonly versionChecks = signal<Map<string, HapiTerminologyVersionCheckResult>>(new Map());

  readonly displayedCols = ['actions', 'displayName', 'version', 'schedulerEnabled', 'frequency', 'executionTime', 'lastRunUtc'];

  ngOnInit(): void {
    this.hub.ensureConnected();

    this.hub.statusChanged$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((event) => this.applyStatusEvent(event.code, event.status));

    // The hub is this screen's only live signal now that the poll is gone, so every (re)connect does one
    // reconciling fetch: a sync that started or finished while the socket was down would otherwise leave a
    // row stuck showing the state it had when the connection dropped.
    this.hub.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.reconcile());

    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: (configs) => {
        this.configs.set(configs);
        this.loading.set(false);
        this.autoScanAndSync(configs);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load terminology-server settings.');
      },
    });
  }

  /** Re-reads every row without touching the loading state — used after a reconnect, where the rows are
   *  already on screen and only their values may have moved on. */
  private reconcile(): void {
    this.svc.getAll().subscribe({
      next: (configs) => this.configs.set(configs),
      error: () => { /* silent: this runs on the hub's initiative, not the user's */ },
    });
  }

  isRunning(code: string): boolean {
    return this.runningCodes().has(code);
  }

  isScanning(code: string): boolean {
    return this.scanningCodes().has(code);
  }

  versionCheck(code: string): HapiTerminologyVersionCheckResult | undefined {
    return this.versionChecks().get(code);
  }

  /**
   * Fires all 13 version checks at once and starts a sync for each system that comes back out of date.
   *
   * Parallel, unlike the batched scan-all endpoint this replaces: that runs its 13 checks sequentially
   * server-side (one shared DbContext), so every row's Version cell stayed blank until the slowest external
   * source answered. One request per system means each row resolves on its own.
   *
   * The checks themselves only compare version strings and download nothing, so running them unattended is
   * safe. The syncs they trigger are NOT cheap — each downloads and imports a full code system, and some
   * (SNOMED CT, ICD-10) are large. Two things keep that in hand: a system already running is never
   * re-started, and one whose credentials are missing is skipped rather than started only to fail. Note the
   * server drains its import queue one job at a time (TerminologyImportBackgroundService), so several
   * outdated systems queue up rather than importing concurrently.
   *
   * To make this opt-in rather than automatic, gate this one method — the ⋮ Run Now and the manual "Scan
   * for updates" button both keep working on their own.
   */
  private autoScanAndSync(configs: readonly HapiTerminologyConfiguration[]): void {
    this.scanningCodes.set(new Set(configs.map((c) => c.code)));

    for (const config of configs) {
      this.svc.scan(config.code).subscribe({
        next: (result) => {
          this.clearScanning(config.code);
          this.versionChecks.update((map) => new Map(map).set(config.code, result));

          if (!result.updateAvailable || this.isRunning(config.code)) {
            return;
          }
          // Checked here as well as in runNow() so a system that cannot possibly succeed is never
          // auto-started — runNow surfaces that as an error toast, which would be noise for a sync nobody
          // asked for. The user still gets the message if they start it themselves.
          if (config.credentials.some((cred) => !cred.hasValue)) {
            return;
          }

          this.startSync(config, { announce: false });
        },
        error: () => {
          this.clearScanning(config.code);
          // Silent: this pass runs on its own initiative, so a failed check is not something the user asked
          // for and failed to get. The manual button reports its own failures.
        },
      });
    }
  }

  /** Checks all 13 systems' official sources for a newer version than what's stored locally — does
   *  not download or import anything, just populates each row's Version column with the result. */
  scanForUpdates(): void {
    this.scanning.set(true);
    this.svc.scanAll().subscribe({
      next: (results) => {
        this.scanning.set(false);
        this.versionChecks.set(new Map(results.map((r) => [r.code, r])));
        const updateCount = results.filter((r) => r.updateAvailable).length;
        if (updateCount > 0) {
          this.toast.success(`${updateCount} code system${updateCount === 1 ? '' : 's'} ${updateCount === 1 ? 'has' : 'have'} a newer version available.`);
        } else {
          this.toast.success('All code systems are up to date.');
        }
      },
      error: () => {
        this.scanning.set(false);
        this.toast.error('Failed to scan for terminology updates.');
      },
    });
  }

  openEdit(config: HapiTerminologyConfiguration): void {
    this.dialog
      .open<HapiTerminologyEditDialogComponent, { config: HapiTerminologyConfiguration }, boolean>(
        HapiTerminologyEditDialogComponent,
        { width: '520px', disableClose: true, data: { config } },
      )
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success(`${config.displayName} settings updated.`);
          this.load();
        }
      });
  }

  openHistory(config: HapiTerminologyConfiguration): void {
    this.dialog.open<HapiTerminologyHistoryDialogComponent, { code: string; displayName: string }, void>(
      HapiTerminologyHistoryDialogComponent,
      { width: '760px', data: { code: config.code, displayName: config.displayName } },
    );
  }

  openCodes(config: HapiTerminologyConfiguration): void {
    this.dialog.open<HapiTerminologyCodesDialogComponent, HapiTerminologyCodesDialogData, void>(
      HapiTerminologyCodesDialogComponent,
      // fillContent (not startMaximized): this is a dense, server-paged table that a centered 900px card
      // clipped. fillContent fills the right-hand content area only, leaving the sidebar visible — the same
      // treatment EHR Endpoints and Allowed Origins get. Maximize still escalates to the full viewport.
      { fillContent: true, maximizable: true, data: { systemCode: config.code, displayName: config.displayName } },
    );
  }

  runNow(config: HapiTerminologyConfiguration): void {
    const missing = config.credentials.filter((c) => !c.hasValue);
    if (missing.length) {
      this.toast.error(
        `${config.displayName} is missing ${missing.map((c) => c.label).join(' and ')} — open Edit to set ${missing.length === 1 ? 'it' : 'them'} before running a sync.`,
      );
      return;
    }

    this.startSync(config, { announce: true });
  }

  /** `announce` is false for the automatic pass: 13 toasts on page load would bury anything that matters.
   *  Either way the row itself shows the sync running, and the hub reports how it ended. */
  private startSync(config: HapiTerminologyConfiguration, options: { announce: boolean }): void {
    // Optimistic, so the row reacts to the click immediately rather than waiting for the server's own
    // "Running" push — which may be a while coming, since imports queue behind one another.
    this.setRunning(config.code, true);
    this.svc.runNow(config.code).subscribe({
      next: () => {
        if (options.announce) {
          this.toast.success(`${config.displayName} sync started.`);
        }
      },
      error: (err: HttpErrorResponse) => {
        this.setRunning(config.code, false);
        if (options.announce) {
          this.toast.error(err.error?.title ?? `Failed to start the ${config.displayName} sync.`);
        }
      },
    });
  }

  /** Applies one pushed status to its row. A terminal status also re-reads that row, since Last Run and the
   *  stored version have just changed server-side. */
  private applyStatusEvent(code: string, status: TerminologyStatus): void {
    if (status === 'Running') {
      this.setRunning(code, true);
      return;
    }

    this.setRunning(code, false);
    this.refreshRow(code);

    const displayName = this.configs().find((c) => c.code === code)?.displayName ?? code;
    if (status === 'Succeeded') {
      this.toast.success(`${displayName} sync completed.`);
    } else if (status === 'Interrupted') {
      // Not a failure of the sync itself — the server stopped underneath it — so it is reported as a
      // warning with the actual cause, rather than implying the data source or configuration is at fault.
      this.toast.error(`${displayName} sync was interrupted by a server restart. Run it again to retry.`);
    } else {
      this.toast.error(`${displayName} sync failed — see History for details.`);
    }
  }

  private refreshRow(code: string): void {
    this.svc.get(code).subscribe((updated) => {
      this.configs.update((list) => list.map((c) => (c.code === code ? updated : c)));
    });
    // Clear this row's stale "update available" badge — the sync that just finished should have
    // brought it current. A fresh scan (not run automatically) will confirm the real latest state.
    this.versionChecks.update((map) => {
      const next = new Map(map);
      next.delete(code);
      return next;
    });
  }

  private setRunning(code: string, running: boolean): void {
    this.runningCodes.update((set) => {
      const next = new Set(set);
      if (running) next.add(code);
      else next.delete(code);
      return next;
    });
  }

  private clearScanning(code: string): void {
    this.scanningCodes.update((set) => {
      const next = new Set(set);
      next.delete(code);
      return next;
    });
  }
}
