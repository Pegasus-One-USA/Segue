import { Component, OnInit, HostListener, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IAppSecretsService } from '../../services/i-app-secrets.service';
import { AppSecret } from '../../models/app-secret.model';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

@Component({
  selector: 'app-app-secret-list',
  standalone: true,
  imports: [
    CommonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './app-secret-list.component.html',
  styleUrls: ['./app-secret-list.component.scss'],
})
export class AppSecretListComponent implements OnInit {
  private readonly svc    = inject(IAppSecretsService);
  private readonly dialog = inject(MatDialog);
  private readonly toast  = inject(ToastService);

  readonly loading = signal(true);
  readonly secrets  = signal<AppSecret[]>([]);

  /** Which row's "more actions" menu is open, if any — keyed by secretName. See toggleActionMenu,
   *  mirrored 1:1 from workflow-list.component.ts's identical row-menu pattern. */
  readonly openActionMenuId = signal<string | null>(null);

  /** Viewport-relative coordinates for the open action menu — see workflow-list.component.ts's
   *  actionMenuPosition for the full rationale (position: fixed escapes .table-wrapper's overflow-x: auto
   *  clipping). */
  readonly actionMenuPosition = signal<{ top?: number; bottom?: number; left: number } | null>(null);

  /** Rough panel height (single Regenerate item + padding) — just needs to be in the right ballpark to
   *  decide whether the panel fits below the trigger, not pixel-exact. */
  private static readonly ACTION_MENU_ESTIMATED_HEIGHT = 70;

  ngOnInit(): void {
    this.load();
  }

  toggleActionMenu(event: MouseEvent, id: string): void {
    if (this.openActionMenuId() === id) {
      this.openActionMenuId.set(null);
      this.actionMenuPosition.set(null);
      return;
    }

    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const left = rect.left;
    const spaceBelow = window.innerHeight - rect.bottom;

    this.actionMenuPosition.set(
      spaceBelow < AppSecretListComponent.ACTION_MENU_ESTIMATED_HEIGHT
        ? { bottom: window.innerHeight - rect.top + 6, left }
        : { top: rect.bottom + 6, left }
    );
    this.openActionMenuId.set(id);
  }

  // Single document:click listener for the whole component — closes the open row menu on any click
  // outside it. Mirrors workflow-list.component.ts's closeFilterMenuIfOutside/onDocumentClick pair.
  private closeActionMenuIfOutside(event: MouseEvent): void {
    if (!(event.target as HTMLElement).closest('.row-menu')) {
      this.openActionMenuId.set(null);
      this.actionMenuPosition.set(null);
    }
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    this.closeActionMenuIfOutside(event);
  }

  load(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: secrets => {
        this.secrets.set(secrets);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load app secrets.');
      },
    });
  }

  confirmRegenerate(secret: AppSecret): void {
    const restartNote = secret.restartRequiredForFullEffect
      ? ' The Worker service must also be restarted afterward, or it may keep using the old value.'
      : '';

    this.dialog
      .open(ConfirmDialogComponent, {
        width: '480px',
        restoreFocus: false,
        data: {
          title: `Regenerate ${secret.displayName}`,
          message:
            `This immediately signs out every signed-in user, including you, and invalidates every ` +
            `previously issued token/link signed with the current value. This cannot be undone.${restartNote}`,
          confirmLabel: 'Regenerate',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.regenerate(secret.secretName).subscribe({
          next: () => {
            this.toast.success(`${secret.displayName} regenerated.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? err.error?.message ?? 'Failed to regenerate secret.';
            this.toast.error(message);
          },
        });
      });
  }
}
