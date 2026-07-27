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

  readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    if (!term) return this.settings();
    return this.settings().filter(
      s => s.key.toLowerCase().includes(term) || (s.description ?? '').toLowerCase().includes(term)
    );
  });

  readonly displayedCols = ['key', 'value', 'description', 'modifiedOnUtc', 'actions'];

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
