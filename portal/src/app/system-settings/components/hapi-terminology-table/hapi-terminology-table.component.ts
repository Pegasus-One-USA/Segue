import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonModule } from '@angular/material/button';
import {
  HapiTerminologyConfiguration,
  HapiTerminologyConfigurationService,
} from '../../services/hapi-terminology-configuration.service';
import { HapiTerminologyEditDialogComponent } from '../../dialogs/hapi-terminology-edit-dialog/hapi-terminology-edit-dialog.component';
import { HapiTerminologyHistoryDialogComponent } from '../../dialogs/hapi-terminology-history-dialog/hapi-terminology-history-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SystemSetting } from '../../models/system-setting.model';
import { SystemSettingDialogComponent, SystemSettingDialogData } from '../../dialogs/system-setting-dialog/system-setting-dialog.component';

const BASE_URL_KEY = 'Terminology:BaseUrl';

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
export class HapiTerminologyTableComponent implements OnInit {
  private readonly svc = inject(HapiTerminologyConfigurationService);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly settingsSvc = inject(ISystemSettingsService);

  readonly loading = signal(true);
  readonly configs = signal<HapiTerminologyConfiguration[]>([]);
  readonly runningCodes = signal<Set<string>>(new Set());
  // Shared across every row below — not per-system, so it's shown once here rather than repeated
  // in each row/dialog. Same key the old Terminology tabs' FHIR API URL points at too.
  readonly baseUrlSetting = signal<SystemSetting | null>(null);

  readonly displayedCols = ['displayName', 'schedulerEnabled', 'frequency', 'executionTime', 'lastRunUtc', 'actions'];

  ngOnInit(): void {
    this.load();
    this.loadBaseUrl();
  }

  private loadBaseUrl(): void {
    this.settingsSvc.getAll().subscribe({
      next: (settings) => {
        this.baseUrlSetting.set(settings.find((s) => s.key === BASE_URL_KEY) ?? null);
      },
      error: () => {},
    });
  }

  editBaseUrl(): void {
    const setting = this.baseUrlSetting();
    if (!setting) return;
    this.dialog
      .open<SystemSettingDialogComponent, SystemSettingDialogData, boolean>(SystemSettingDialogComponent, {
        width: '520px',
        disableClose: true,
        data: { mode: 'edit', setting },
      })
      .afterClosed()
      .subscribe((saved) => {
        if (saved) {
          this.toast.success('Terminology server URL updated.');
          this.loadBaseUrl();
        }
      });
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

  runNow(config: HapiTerminologyConfiguration): void {
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
          setTimeout(() => this.pollUntilSettled(code), HISTORY_POLL_INTERVAL_MS);
          return;
        }
        this.setRunning(code, false);
        this.refreshRow(code);
      },
      error: () => this.setRunning(code, false),
    });
  }

  private refreshRow(code: string): void {
    this.svc.get(code).subscribe((updated) => {
      this.configs.update((list) => list.map((c) => (c.code === code ? updated : c)));
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
