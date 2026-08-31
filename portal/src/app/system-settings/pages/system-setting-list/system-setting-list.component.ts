import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SystemSetting } from '../../models/system-setting.model';
import { SystemSettingDialogComponent, SystemSettingDialogData } from '../../dialogs/system-setting-dialog/system-setting-dialog.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { terminologyCodeOf, generalSettingGroupOf } from '../../utils/terminology-setting-field';
import { DialogService } from '../../../core/services/dialog.service';
import { HapiTerminologyTableComponent } from '../../components/hapi-terminology-table/hapi-terminology-table.component';

// These now render in their own dedicated table (HapiTerminologyTableComponent, above this generic
// list) with grouped settings, Run Now, and History — excluded here so they don't appear twice.
const HAPI_TERMINOLOGY_KEY_PATTERN = /^Terminology:\w+Hapi:/;

// LOINC/NDC/RxNorm/SNOMED CT/UCUM's non-Hapi settings already have a proper dedicated settings page
// each (Settings → System Settings → Terminology → ...) — the raw key/value rows here are a
// redundant, worse-labeled third way to edit the same data, so they're hidden. LOINC's one setting
// that genuinely matters to the HAPI sync too (DownloadApiUrl) now lives in the HAPI LOINC edit
// modal instead (see HapiTerminologySystemRegistry.DownloadApiUrlSettingKey) — everything else in
// LOINC's legacy group is either superseded by LoincHapi:* or unused by any sync code at all.
const LEGACY_TERMINOLOGY_KEY_PATTERN = /^Terminology:(Loinc|Ndc|RxNorm|Snomed|Ucum):/;

// Now has a dedicated inline Edit button in the new table's header card (it's the one setting
// genuinely shared across all 13 rows) — hidden here so there isn't a second, redundant way to edit it.
const BASE_URL_KEY = 'Terminology:BaseUrl';

interface GroupHeaderRow {
  isGroupHeader: true;
  label: string;
}

interface CodeGroupHeaderRow {
  isCodeGroupHeader: true;
  code: string;
  label: string;
  collapsed: boolean;
}

type GroupedRow = SystemSetting | GroupHeaderRow | CodeGroupHeaderRow;

@Component({
  selector: 'app-system-setting-list',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatMenuModule,
    HapiTerminologyTableComponent,
  ],
  templateUrl: './system-setting-list.component.html',
  styleUrls: ['./system-setting-list.component.scss'],
})
export class SystemSettingListComponent implements OnInit {
  private readonly svc    = inject(ISystemSettingsService);
  private readonly customDialog = inject(DialogService);
  private readonly toast  = inject(ToastService);

  readonly loading  = signal(true);
  readonly settings = signal<SystemSetting[]>([]);
  readonly search   = signal('');

  // ── Decrypt Provisioned Secret tool ─────────────────────────────────────────
  // Recovery tool for a private key PEM (or other app-provisioned secret) whose owning SourceConnection/row is
  // gone, unreachable, or lives in a place the admin can't otherwise read it back from. Only ever decrypts a
  // value encrypted by THIS instance's own Data Protection key ring — a ProtectedValue copied from a different
  // FHIRBridge deployment will be rejected by the API (key rings are per-instance, not shared).
  readonly protectedValueInput = signal('');
  readonly decrypting = signal(false);
  readonly decryptError = signal<string | null>(null);
  readonly decryptedPlaintext = signal<string | null>(null);

  decryptProvisionedSecret(): void {
    const protectedValue = this.protectedValueInput().trim();
    if (!protectedValue) {
      this.decryptError.set('Paste a ProtectedValue first.');
      return;
    }

    this.decrypting.set(true);
    this.decryptError.set(null);
    this.decryptedPlaintext.set(null);
    this.svc.decryptProvisionedSecret(protectedValue).subscribe({
      next: (result) => {
        this.decrypting.set(false);
        this.decryptedPlaintext.set(result.plaintextValue);
      },
      error: (err: HttpErrorResponse) => {
        this.decrypting.set(false);
        this.decryptError.set(
          err.error?.error_description ?? err.error?.title ?? 'Failed to decrypt this value.'
        );
      },
    });
  }

  // Client-side-only download — the plaintext already reached the browser in decryptedPlaintext(); this just
  // hands it back to the admin as a file instead of a copy/paste, matching how a real private key PEM is
  // normally distributed (a .pem file, not a text blob left sitting in the page).
  downloadDecryptedPem(): void {
    const plaintext = this.decryptedPlaintext();
    if (!plaintext) return;

    const blob = new Blob([plaintext], { type: 'application/x-pem-file' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'decrypted-key.pem';
    anchor.click();
    URL.revokeObjectURL(url);
  }

  clearDecryptTool(): void {
    this.protectedValueInput.set('');
    this.decryptError.set(null);
    this.decryptedPlaintext.set(null);
  }

  /** Only one sortable column today — "Action on" (createdOnUtc, or modifiedOnUtc when later). */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
  }

  private static actionOnOf(s: SystemSetting): number {
    const value = s.modifiedOnUtc || s.createdOnUtc;
    return value ? new Date(value).getTime() : 0;
  }

  readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const rows = !term ? this.settings() : this.settings().filter(
      s => s.key.toLowerCase().includes(term) || (s.description ?? '').toLowerCase().includes(term)
    );

    const direction = this.actionOnSortDirection();
    if (!direction) return rows;
    const sorted = [...rows].sort((a, b) => SystemSettingListComponent.actionOnOf(a) - SystemSettingListComponent.actionOnOf(b));
    return direction === 'desc' ? sorted.reverse() : sorted;
  });

  readonly displayedCols = ['actions', 'key', 'value', 'description', 'actionBy', 'modifiedOnUtc'];

  // ── Group headings ──────────────────────────────────────────────────────────
  // Purely a display grouping. "Terminology:*" keys (LOINC/SNOMED/RxNorm/ICD-10/HAPI-sync
  // settings, etc.) render under their own heading, ahead of every other setting; everything
  // else groups under "General Settings". Within each heading, settings further collapse into
  // one sub-heading per code/prefix (e.g. "CVX (HAPI terminology server)", "Anomaly Detection") —
  // collapsed by default — using the same expand/collapse chevron convention as the execution
  // history detail page, so a growing list of config doesn't have to be scanned flatly.
  private readonly collapsedCodes = signal<Set<string>>(new Set());

  // Namespaced so a General Settings prefix can never collide with a terminology code in the
  // shared collapsedCodes set (e.g. a hypothetical "Workflow" terminology code vs. the
  // "Workflow:*" general prefix).
  private static subGroupOf(key: string, isTerminology: boolean): { code: string; label: string } | null {
    const info = isTerminology ? terminologyCodeOf(key) : generalSettingGroupOf(key);
    if (!info) return null;
    return { code: `${isTerminology ? 'term' : 'gen'}:${info.code}`, label: info.label };
  }

  private buildSection(rows: SystemSetting[], isTerminology: boolean, collapsed: Set<string>): GroupedRow[] {
    const byCode = new Map<string, { label: string; rows: SystemSetting[] }>();
    const ungrouped: SystemSetting[] = [];
    for (const setting of rows) {
      const groupInfo = SystemSettingListComponent.subGroupOf(setting.key, isTerminology);
      if (!groupInfo) { ungrouped.push(setting); continue; }
      const entry = byCode.get(groupInfo.code) ?? { label: groupInfo.label, rows: [] };
      entry.rows.push(setting);
      byCode.set(groupInfo.code, entry);
    }

    const result: GroupedRow[] = [...ungrouped];
    for (const [code, entry] of [...byCode.entries()].sort((a, b) => a[1].label.localeCompare(b[1].label))) {
      const isCollapsed = collapsed.has(code);
      result.push({ isCodeGroupHeader: true, code, label: entry.label, collapsed: isCollapsed });
      if (!isCollapsed) result.push(...entry.rows);
    }
    return result;
  }

  private subGroupCodes(rows: SystemSetting[], isTerminology: boolean): Set<string> {
    const codes = new Set<string>();
    for (const s of rows) {
      const groupInfo = SystemSettingListComponent.subGroupOf(s.key, isTerminology);
      if (groupInfo) codes.add(groupInfo.code);
    }
    return codes;
  }

  readonly groupedRows = computed<GroupedRow[]>(() => {
    const rows = this.filtered();
    const collapsed = this.collapsedCodes();
    const terminology = rows.filter(s => s.key.startsWith('Terminology:'));
    const other = rows.filter(s => !s.key.startsWith('Terminology:'));

    const result: GroupedRow[] = [];
    if (terminology.length) {
      result.push({ isGroupHeader: true, label: 'Terminology Settings' });
      result.push(...this.buildSection(terminology, true, collapsed));
    }
    if (other.length) {
      result.push({ isGroupHeader: true, label: 'General Settings' });
      result.push(...this.buildSection(other, false, collapsed));
    }
    return result;
  });

  readonly terminologyCodes = computed(() =>
    this.subGroupCodes(this.filtered().filter(s => s.key.startsWith('Terminology:')), true));

  readonly generalCodes = computed(() =>
    this.subGroupCodes(this.filtered().filter(s => !s.key.startsWith('Terminology:')), false));

  readonly allTerminologyCollapsed = computed(() => {
    const codes = this.terminologyCodes();
    if (codes.size === 0) return true;
    const collapsed = this.collapsedCodes();
    return [...codes].every(code => collapsed.has(code));
  });

  readonly allGeneralCollapsed = computed(() => {
    const codes = this.generalCodes();
    if (codes.size === 0) return true;
    const collapsed = this.collapsedCodes();
    return [...codes].every(code => collapsed.has(code));
  });

  toggleCode(code: string): void {
    this.collapsedCodes.update(set => {
      const next = new Set(set);
      next.has(code) ? next.delete(code) : next.add(code);
      return next;
    });
  }

  toggleAllTerminology(): void {
    this.toggleAllFor(this.terminologyCodes(), this.allTerminologyCollapsed());
  }

  toggleAllGeneral(): void {
    this.toggleAllFor(this.generalCodes(), this.allGeneralCollapsed());
  }

  private toggleAllFor(codes: Set<string>, currentlyAllCollapsed: boolean): void {
    this.collapsedCodes.update(set => {
      const next = new Set(set);
      for (const code of codes) {
        currentlyAllCollapsed ? next.delete(code) : next.add(code);
      }
      return next;
    });
  }

  isGroupHeaderRow(_index: number, row: GroupedRow): row is GroupHeaderRow {
    return 'isGroupHeader' in row;
  }

  isCodeGroupHeaderRow(_index: number, row: GroupedRow): row is CodeGroupHeaderRow {
    return 'isCodeGroupHeader' in row;
  }

  isDataRow(_index: number, row: GroupedRow): row is SystemSetting {
    return !('isGroupHeader' in row) && !('isCodeGroupHeader' in row);
  }

  ngOnInit(): void {
    this.load();
  }

  // Collapsed-by-default only on the very first load — a later reload (after saving/deleting one
  // setting) must not reset whatever the admin currently has expanded.
  private collapseDefaultsApplied = false;

  load(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: settings => {
        this.settings.set(settings.filter(s =>
          !HAPI_TERMINOLOGY_KEY_PATTERN.test(s.key) && !LEGACY_TERMINOLOGY_KEY_PATTERN.test(s.key) && s.key !== BASE_URL_KEY));
        this.loading.set(false);

        if (!this.collapseDefaultsApplied) {
          this.collapseDefaultsApplied = true;
          const codes = new Set<string>();
          for (const s of settings) {
            const groupInfo = SystemSettingListComponent.subGroupOf(s.key, s.key.startsWith('Terminology:'));
            if (groupInfo) codes.add(groupInfo.code);
          }
          this.collapsedCodes.set(codes);
        }
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load system settings.');
      },
    });
  }

  openAdd(): void {
    this.customDialog
      .open<SystemSettingDialogComponent, SystemSettingDialogData, boolean>(SystemSettingDialogComponent, {
        width: '520px',
        disableClose: true,
        data: { mode: 'create' },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Setting saved.');
          this.load();
        }
      });
  }

  openEdit(setting: SystemSetting): void {
    this.customDialog
      .open<SystemSettingDialogComponent, SystemSettingDialogData, boolean>(SystemSettingDialogComponent, {
        width: '520px',
        disableClose: true,
        data: { mode: 'edit', setting },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success(`"${setting.key}" updated.`);
          this.load();
        }
      });
  }

  confirmDelete(setting: SystemSetting): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: 'Remove Setting Override',
          message: `Remove the DB override for "${setting.key}"? It will revert to its appsettings/code default on next read.`,
          confirmLabel: 'Remove',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(setting.key).subscribe({
          next: () => {
            this.toast.success(`"${setting.key}" reverted to its default.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to remove setting.';
            this.toast.error(message);
          },
        });
      });
  }
}
