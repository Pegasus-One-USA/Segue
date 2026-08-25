import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { SystemSetting } from '../../models/system-setting.model';
import { SystemSettingDialogComponent } from '../../dialogs/system-setting-dialog/system-setting-dialog.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

interface GroupHeaderRow {
  isGroupHeader: true;
  label: string;
}

type GroupedRow = SystemSetting | GroupHeaderRow;

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
  ],
  templateUrl: './system-setting-list.component.html',
  styleUrls: ['./system-setting-list.component.scss'],
})
export class SystemSettingListComponent implements OnInit {
  private readonly svc    = inject(ISystemSettingsService);
  private readonly dialog = inject(MatDialog);
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

  readonly displayedCols = ['key', 'value', 'description', 'actionBy', 'modifiedOnUtc', 'actions'];

  // ── Group headings ──────────────────────────────────────────────────────────
  // Purely a display grouping — "Terminology:*" keys (LOINC/SNOMED/RxNorm/ICD-10/HAPI-sync settings,
  // etc.) render under their own heading, ahead of every other setting, so the growing list of
  // terminology-server config doesn't just blend into one undifferentiated table.
  readonly groupedRows = computed<GroupedRow[]>(() => {
    const rows = this.filtered();
    const terminology = rows.filter(s => s.key.startsWith('Terminology:'));
    const other = rows.filter(s => !s.key.startsWith('Terminology:'));

    const result: GroupedRow[] = [];
    if (terminology.length) {
      result.push({ isGroupHeader: true, label: 'Terminology Settings' }, ...terminology);
    }
    if (other.length) {
      result.push({ isGroupHeader: true, label: 'General Settings' }, ...other);
    }
    return result;
  });

  isGroupHeaderRow(_index: number, row: GroupedRow): row is GroupHeaderRow {
    return 'isGroupHeader' in row;
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: settings => {
        this.settings.set(settings);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load system settings.');
      },
    });
  }

  openAdd(): void {
    this.dialog
      .open(SystemSettingDialogComponent, {
        width: '520px',
        disableClose: true,
        restoreFocus: false,
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
    this.dialog
      .open(SystemSettingDialogComponent, {
        width: '520px',
        disableClose: true,
        restoreFocus: false,
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
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '440px',
        restoreFocus: false,
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
