import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonModule } from '@angular/material/button';
import {
  HapiTerminologyConfiguration,
  HapiTerminologyConfigurationService,
  HapiTerminologyVersionCheckResult,
} from '../../services/hapi-terminology-configuration.service';
import { HapiTerminologyEditDialogComponent } from '../../dialogs/hapi-terminology-edit-dialog/hapi-terminology-edit-dialog.component';
import { HapiTerminologyHistoryDialogComponent } from '../../dialogs/hapi-terminology-history-dialog/hapi-terminology-history-dialog.component';
import { HapiTerminologyCodesDialogComponent, HapiTerminologyCodesDialogData } from '../../dialogs/hapi-terminology-codes-dialog/hapi-terminology-codes-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';

const HISTORY_POLL_INTERVAL_MS = 3000;

/**
 * One row per HAPI-terminology-server sync system (CVX/DCM/HCPCS/ICD-10-CM/ICD-10-PCS/ICD-11/ICPC-3/
 * LOINC/MeSH/NDC/RxNorm/SNOMED CT/UCUM), replacing the old flat "Terminology:*Hapi:*" settings rows.
 * Rendered on the same General Settings page, above the generic settings table.
 */
@Component({
  selector: 'app-hapi-terminology-table',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatIconModule, MatMenuModule, MatButtonModule],
  templateUrl: './hapi-terminology-table.component.html',
  styleUrls: ['./hapi-terminology-table.component.scss'],
})
export class HapiTerminologyTableComponent implements OnInit, OnDestroy {
  private readonly svc = inject(HapiTerminologyConfigurationService);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  /** Bound to the parent page's shared search box, so one search filters both General Settings
   *  groups and this table's rows instead of needing a second search field. */
  readonly searchTerm = input('');

  readonly loading = signal(true);
  readonly configs = signal<HapiTerminologyConfiguration[]>([]);
  readonly filteredConfigs = computed(() => {
    const term = this.searchTerm().trim().toLowerCase();
    if (!term) return this.configs();
    return this.configs().filter(c => c.displayName.toLowerCase().includes(term) || c.code.toLowerCase().includes(term));
  });
  readonly runningCodes = signal<Set<string>>(new Set());
  readonly scanning = signal(false);
  readonly versionChecks = signal<Map<string, HapiTerminologyVersionCheckResult>>(new Map());

  readonly displayedCols = ['actions', 'displayName', 'version', 'schedulerEnabled', 'frequency', 'executionTime', 'lastRunUtc'];

  private readonly pollTimers = new Map<string, ReturnType<typeof setTimeout>>();

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    for (const timer of this.pollTimers.values()) {
      clearTimeout(timer);
    }
    this.pollTimers.clear();
  }

  load(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: (configs) => {
        this.configs.set(configs);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load terminology-server settings.');
      },
    });
  }

  isRunning(code: string): boolean {
    return this.runningCodes().has(code);
  }

  versionCheck(code: string): HapiTerminologyVersionCheckResult | undefined {
    return this.versionChecks().get(code);
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
      { width: '900px', maximizable: true, data: { systemCode: config.code, displayName: config.displayName } },
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

    this.setRunning(config.code, true);
    this.svc.runNow(config.code).subscribe({
      next: () => {
        this.toast.success(`${config.displayName} sync started — check History for progress.`);
        this.pollUntilSettled(config.code);
      },
      error: (err: HttpErrorResponse) => {
        this.setRunning(config.code, false);
        this.toast.error(err.error?.title ?? `Failed to start the ${config.displayName} sync.`);
      },
    });
  }

  private pollUntilSettled(code: string): void {
    this.svc.getHistory(code).subscribe({
      next: (entries) => {
        if (entries.some((e) => e.status === 'Running')) {
          this.pollTimers.set(
            code,
            setTimeout(() => this.pollUntilSettled(code), HISTORY_POLL_INTERVAL_MS),
          );
          return;
        }
        this.pollTimers.delete(code);
        this.setRunning(code, false);
        this.refreshRow(code);
      },
      error: () => {
        this.pollTimers.delete(code);
        this.setRunning(code, false);
      },
    });
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
}
