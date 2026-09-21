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
import { SettingsPageDialogService, SettingsPageDialogKey } from '../../../settings/services/settings-page-dialog.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { FullAccessResolverService } from '../../../auth/services/full-access-resolver.service';
import { HapiTerminologyTableComponent } from '../../components/hapi-terminology-table/hapi-terminology-table.component';
import { TERMINOLOGY_SERVER_TABLE_ENABLED } from '../../../data/terminology-feature.config';
import { GeneralSettingGroupDialogComponent, GeneralSettingGroupDialogData } from '../../dialogs/general-setting-group-dialog/general-setting-group-dialog.component';

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

// Every "License:*" key is managed on its own dedicated screen now, not as generic key/value rows here:
// "License:Token" is live data written by the License screen's Activate flow (LicenseService.ApplyAsync
// upserts it) — hand-editing a signed token through a free-text field can only invalidate it —  and
// "License:LicensorApplicationUrl" has its own field on the License screen's License Request tab, so
// showing it again here as a generic "License" group would just be a second, redundant place to edit it.
//
// Hidden from this list only. Both rows stay in the database.
const LICENSE_TOKEN_KEY_PATTERN = /^License:/;

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

// General Settings groups (e.g. "Alert Evaluation") render as one directly-actionable row rather
// than an expandable accordion — see GeneralSettingGroupDialogComponent. Terminology's remaining
// CodeGroupHeaderRow behavior stays as-is for any future terminology grouping that isn't already
// covered by the dedicated Hapi table.
interface GeneralGroupRow {
  isGeneralGroup: true;
  code: string;
  label: string;
  settings: SystemSetting[];
  lastModifiedOnUtc: string | null;
}

// A row that opens an existing full PAGE component in a dialog rather than editing SystemSetting keys —
// for configuration that has its own screen and its own API (License), not a set of key/value rows.
// Static: unlike every other row type here, it is not derived from settings data.
interface LauncherRow {
  isLauncher: true;
  code: SettingsPageDialogKey;
  label: string;
  summary: string;
  /** Hidden unless the viewer is SuperAdmin / Full System Access. */
  superAdminOnly?: boolean;
  /** OR-list of permission codes that reveal this row. Omitted means "anyone who reached this page". */
  permissions?: string[];
}

// Setting-group prefixes only a SuperAdmin (or a Full System Access role) may see or edit.
//
// This page is no longer SuperAdmin-only as a whole — it is reachable with configuration.view/write so
// operational groups (worker intervals, caching, workflow numbering) can be managed without elevating
// someone to SuperAdmin. These groups are the ones where that would be a security downgrade: credentials
// and token config, MFA enforcement, audit-chain integrity, abuse protection, compliance controls, and
// terminology (which carries external download credentials/URLs).
//
// UI-ONLY. SystemSettingsController authorizes every key with the same configuration.* policy, so a caller
// holding configuration.write can still change these by calling the API directly. Enforcing it for real
// needs per-key gating server-side.
const SUPER_ADMIN_ONLY_GROUPS: readonly string[] = [
  'Authentication',
  'LocalAuth',
  'Mfa',
  'Compliance',
  'RateLimiting',
  'AuditChainVerification',
  'Terminology',
  // A wrong value here breaks every EHR OAuth integration instantly (see OAuthController.PublicOriginAsync)
  // — same trust level as Allowed Origins, which is also SuperAdmin-only.
  'OAuth',
];

type GroupedRow = SystemSetting | GroupHeaderRow | CodeGroupHeaderRow | GeneralGroupRow | LauncherRow;

// Rows on this page that open a full existing page-component in a dialog. License moved here from its own
// Settings nav entry; the route was removed, so this row is now the ONLY way in — which also means License
// is SuperAdmin-only now, matching this page's own guard (it was previously roleGuard SuperAdmin|Admin).
const LAUNCHER_ROWS: readonly LauncherRow[] = [
  {
    isLauncher: true,
    code: 'license',
    label: 'License',
    summary: 'Status, restrictions, usage and activation',
    superAdminOnly: true,
  },
  {
    isLauncher: true,
    code: 'ehr-endpoints',
    label: 'EHR Endpoints',
    summary: 'Vendor endpoints workflows connect through',
    // Its own former route guard — this row is how an ehrendpoints.view holder reaches the screen now
    // that /settings/ehr-endpoints is gone.
    permissions: ['ehrendpoints.view'],
  },
  {
    isLauncher: true,
    code: 'allowed-origins',
    label: 'Allowed Origins',
    summary: 'CORS origins permitted to call the API',
    // Was superAdminGuard as a route; unchanged here.
    superAdminOnly: true,
  },
  {
    isLauncher: true,
    code: 'email',
    label: 'Email',
    summary: 'SMTP delivery and notification sender',
    // Same permission pair its own route/nav tab used.
    permissions: ['configuration.view', 'configuration.write'],
  },
  {
    isLauncher: true,
    code: 'security',
    label: 'Security',
    summary: 'Application secrets and key management',
    superAdminOnly: true,
  },
  {
    isLauncher: true,
    code: 'sso-configurations',
    label: 'SSO Configurations',
    summary: 'External identity providers for single sign-on',
    superAdminOnly: true,
  },
];

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
  private readonly settingsPageDialog = inject(SettingsPageDialogService);
  private readonly authStore = inject(AuthStore);

  /** Whether the "Terminology Server" table (the 13 HAPI-synced code systems) is switched on — see
   *  data/terminology-feature.config.ts. Deliberately NOT TERMINOLOGY_FEATURE_ENABLED: that flag
   *  governs the separate, newer Terminology Codes tab, which stays off while this table stays on.
   *  Template-only: it gates the HAPI terminology table, whose ngOnInit otherwise fires
   *  GET /api/v1/terminology/hapi on every load of this page for a feature that is turned off. */
  protected readonly terminologyServerTableEnabled = TERMINOLOGY_SERVER_TABLE_ENABLED;
  private readonly fullAccessSvc = inject(FullAccessResolverService);

  // Same elevated-access resolution the System Settings shell uses: a literal SuperAdmin claim answers
  // synchronously, and any other role is checked once against the Full System Access role list. Starts
  // false so restricted rows are hidden until proven otherwise — fail closed, never flash them.
  private readonly callerHasFullAccess = signal(false);

  /** True for SuperAdmin or a role carrying Full System Access. */
  readonly isElevated = computed(() => this.authStore.hasRole('SuperAdmin') || this.callerHasFullAccess());

  /** True for a viewer who may see the SystemSetting key/value groups at all. The page is also reachable
   *  with only ehrendpoints.view (that permission exists to open the EHR Endpoints row, which moved here
   *  from its own route) — such a viewer must not see unrelated configuration rows. */
  readonly canViewSettingGroups = computed(() =>
    this.isElevated() || this.authStore.isAdmin()
    || this.authStore.hasPermission('configuration.view')
    || this.authStore.hasPermission('configuration.write'));

  /** Whether a launcher row / setting group is visible to this viewer. */
  private canSee(rule: { superAdminOnly?: boolean; permissions?: string[] }): boolean {
    if (rule.superAdminOnly) return this.isElevated();
    if (!rule.permissions?.length) return true;
    return this.isElevated() || this.authStore.isAdmin()
      || rule.permissions.some(permission => this.authStore.hasPermission(permission));
  }
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
    // A setting also matches when its GROUP's label matches (e.g. searching "alert" should keep
    // every AlertEvaluation:* row) — otherwise a group-label-only match would drop sibling settings
    // out of buildSection()'s entry.rows, leaving the group's Edit dialog showing a partial field set.
    const rows = !term ? this.settings() : this.settings().filter(s => {
      if (s.key.toLowerCase().includes(term) || (s.description ?? '').toLowerCase().includes(term)) return true;
      const groupInfo = s.key.startsWith('Terminology:') ? terminologyCodeOf(s.key) : generalSettingGroupOf(s.key);
      return !!groupInfo && groupInfo.label.toLowerCase().includes(term);
    });

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
      if (isTerminology) {
        const isCollapsed = collapsed.has(code);
        result.push({ isCodeGroupHeader: true, code, label: entry.label, collapsed: isCollapsed });
        if (!isCollapsed) result.push(...entry.rows);
      } else {
        result.push({
          isGeneralGroup: true,
          code,
          label: entry.label,
          settings: entry.rows,
          lastModifiedOnUtc: SystemSettingListComponent.latestModifiedOf(entry.rows),
        });
      }
    }
    return result;
  }

  private static latestModifiedOf(rows: SystemSetting[]): string | null {
    const dates = rows.map(r => r.modifiedOnUtc ?? r.createdOnUtc).filter((d): d is string => !!d);
    return dates.length ? dates.reduce((a, b) => (a > b ? a : b)) : null;
  }

  readonly groupedRows = computed<GroupedRow[]>(() => {
    const rows = this.filtered();
    const collapsed = this.collapsedCodes();
    // "Terminology:*" settings no longer render here — this page (General) is not the right place
    // for them; they're managed under Settings > System Settings > Terminology Codes instead (that
    // route/its dedicated per-code-system pages were never removed, just unlinked from nav — restore
    // system-settings-shell.component.ts's commented-out 'Terminology Codes' nav entry to reach them).
    const result: GroupedRow[] = [];

    // Launcher rows participate in the same free-text search as everything else, so typing "license"
    // finds it; they are not SystemSettings, so they're matched on their own label/summary here.
    const term = this.search().trim().toLowerCase();
    const visibleLaunchers = LAUNCHER_ROWS.filter(row => this.canSee(row));
    const launchers = term
      ? visibleLaunchers.filter(row =>
          row.label.toLowerCase().includes(term) || row.summary.toLowerCase().includes(term))
      : visibleLaunchers;
    result.push(...launchers);

    // An ehrendpoints.view-only viewer reaches this page solely to open the EHR Endpoints launcher —
    // the configuration key/value groups are not theirs to see.
    if (!this.canViewSettingGroups()) {
      return result;
    }

    const other = rows
      .filter(s => !s.key.startsWith('Terminology:'))
      // Restricted groups are removed from the data itself, not just hidden in the template, so a
      // non-elevated viewer can neither see their values nor open their edit dialog. Keys with no
      // group prefix stay visible: they are ungrouped one-off rows, none of which are restricted.
      .filter(s => this.isElevated() || !SUPER_ADMIN_ONLY_GROUPS.includes(s.key.split(':')[0]));

    if (other.length) {
      result.push(...this.buildSection(other, false, collapsed));
    }
    return result;
  });

  // Reusable scaffolding for a future collapsible terminology-style grouping (see subGroupOf/
  // buildSection's isTerminology branch) — not currently invoked, since Terminology Settings no
  // longer renders on this page (see groupedRows()).
  toggleCode(code: string): void {
    this.collapsedCodes.update(set => {
      const next = new Set(set);
      if (next.has(code)) { next.delete(code); } else { next.add(code); }
      return next;
    });
  }

  isGroupHeaderRow(_index: number, row: GroupedRow): row is GroupHeaderRow {
    return 'isGroupHeader' in row;
  }

  isCodeGroupHeaderRow(_index: number, row: GroupedRow): row is CodeGroupHeaderRow {
    return 'isCodeGroupHeader' in row;
  }

  isGeneralGroupRow(_index: number, row: GroupedRow): row is GeneralGroupRow {
    return 'isGeneralGroup' in row;
  }

  isLauncherRow(_index: number, row: GroupedRow): row is LauncherRow {
    return 'isLauncher' in row;
  }

  isDataRow(_index: number, row: GroupedRow): row is SystemSetting {
    return !('isGroupHeader' in row) && !('isCodeGroupHeader' in row) && !('isGeneralGroup' in row)
      && !('isLauncher' in row);
  }

  constructor() {
    // A literal SuperAdmin claim already satisfies isElevated() on its own — skip the extra API call
    // for that common case, exactly as the System Settings shell does.
    if (this.authStore.hasRole('SuperAdmin')) return;

    const heldRoleNames = new Set(this.authStore.roles().map(role => role.name));
    this.fullAccessSvc.resolve(heldRoleNames)
      .subscribe(hasFullAccess => this.callerHasFullAccess.set(hasFullAccess));
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
          !HAPI_TERMINOLOGY_KEY_PATTERN.test(s.key)
          && !LEGACY_TERMINOLOGY_KEY_PATTERN.test(s.key)
          && !LICENSE_TOKEN_KEY_PATTERN.test(s.key)));
        this.loading.set(false);

        if (!this.collapseDefaultsApplied) {
          this.collapseDefaultsApplied = true;
          // Only terminology groups use collapse state now — General Settings groups render as a
          // single actionable row each (see GeneralGroupRow), not an expandable accordion.
          const codes = new Set<string>();
          for (const s of settings) {
            if (!s.key.startsWith('Terminology:')) continue;
            const groupInfo = SystemSettingListComponent.subGroupOf(s.key, true);
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

  openEditGroup(row: GeneralGroupRow): void {
    this.customDialog
      .open<GeneralSettingGroupDialogComponent, GeneralSettingGroupDialogData, boolean>(GeneralSettingGroupDialogComponent, {
        width: '560px',
        disableClose: true,
        data: { label: row.label, settings: row.settings },
      })
      .afterClosed()
      .subscribe(saved => {
        if (saved) {
          this.toast.success(`${row.label} settings updated.`);
          this.load();
        }
      });
  }

  /** Opens a launcher row's page component as a full, edge-to-edge dialog — the same treatment Source and
   *  Destination Connections give their own screens (fillContent + maximizable), so a dense page gets the full
   *  content area with proper internal scrolling instead of being clipped inside a small centered card.
   *  The hosted component is the UNCHANGED page component, so behaviour matches the old menu entry exactly. */
  openLauncher(row: LauncherRow): void {
    // Shared opener, so every entry point (this row, the app shell's license banners, the dev-mint page)
    // lands on the identical dialog.
    this.settingsPageDialog.open(row.code);
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
