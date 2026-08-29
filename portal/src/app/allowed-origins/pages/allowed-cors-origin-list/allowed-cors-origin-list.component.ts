import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IAllowedCorsOriginService } from '../../services/i-allowed-cors-origin.service';
import { AllowedCorsOrigin } from '../../models/allowed-cors-origin.model';
import { AllowedCorsOriginDialogComponent } from '../../dialogs/allowed-cors-origin-dialog/allowed-cors-origin-dialog.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

@Component({
  selector: 'app-allowed-cors-origin-list',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './allowed-cors-origin-list.component.html',
  styleUrls: ['./allowed-cors-origin-list.component.scss'],
})
export class AllowedCorsOriginListComponent implements OnInit {
  private readonly svc    = inject(IAllowedCorsOriginService);
  private readonly dialog = inject(MatDialog);
  private readonly toast  = inject(ToastService);

  readonly loading = signal(true);
  readonly origins  = signal<AllowedCorsOrigin[]>([]);

  readonly displayedCols = ['originUrl', 'label', 'createdOnUtc', 'createdBy', 'actions'];

  ngOnInit(): void {
    this.loadOrigins();
  }

  loadOrigins(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: origins => {
        this.origins.set(origins);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load allowed origins.');
      },
    });
  }

  openAdd(): void {
    this.dialog
      .open(AllowedCorsOriginDialogComponent, {
        width: '480px',
        disableClose: true,
        restoreFocus: false,
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Origin added — takes effect immediately, no restart needed.');
          this.loadOrigins();
        }
      });
  }

  confirmDelete(origin: AllowedCorsOrigin): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Remove Allowed Origin',
          message: `Are you sure you want to remove "${origin.originUrl}"? Browsers at this origin will lose API access immediately.`,
          confirmLabel: 'Remove',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(origin.id).subscribe({
          next: () => {
            this.toast.success(`"${origin.originUrl}" removed.`);
            this.loadOrigins();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to remove origin.';
            this.toast.error(message);
          },
        });
      });
  }
}
