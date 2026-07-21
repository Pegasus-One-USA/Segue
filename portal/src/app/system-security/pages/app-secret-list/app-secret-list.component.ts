import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IAppSecretsService } from '../../services/i-app-secrets.service';
import { AppSecret } from '../../models/app-secret.model';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-app-secret-list',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './app-secret-list.component.html',
  styleUrls: ['./app-secret-list.component.scss'],
})
export class AppSecretListComponent implements OnInit {
  private readonly svc    = inject(IAppSecretsService);
  private readonly dialog = inject(MatDialog);
  private readonly snack  = inject(MatSnackBar);

  readonly loading = signal(true);
  readonly secrets  = signal<AppSecret[]>([]);

  readonly displayedCols = ['displayName', 'provisioned', 'lastRotatedUtc', 'actions'];

  ngOnInit(): void {
    this.load();
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
        this.snack.open('Failed to load app secrets.', 'Dismiss', { duration: 4000 });
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
            this.snack.open(`${secret.displayName} regenerated.`, 'Dismiss', { duration: 4000 });
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? err.error?.message ?? 'Failed to regenerate secret.';
            this.snack.open(message, 'Dismiss', { duration: 5000 });
          },
        });
      });
  }
}
