import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IAllowedCorsOriginService } from '../../services/i-allowed-cors-origin.service';
import { AllowedCorsOrigin } from '../../models/allowed-cors-origin.model';
import { AllowedCorsOriginDialogComponent } from '../../dialogs/allowed-cors-origin-dialog/allowed-cors-origin-dialog.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';

@Component({
  selector: 'app-allowed-cors-origin-list',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './allowed-cors-origin-list.component.html',
  styleUrls: ['./allowed-cors-origin-list.component.scss'],
})
export class AllowedCorsOriginListComponent implements OnInit {
  private readonly svc    = inject(IAllowedCorsOriginService);
  private readonly customDialog = inject(DialogService);
  private readonly toast  = inject(ToastService);

  readonly loading = signal(true);
  readonly reloading = signal(false);
  readonly origins  = signal<AllowedCorsOrigin[]>([]);

  /** Free-text filter on the Label column. Applied client-side: unlike the other listings this one has no
   *  paged/filtered endpoint to defer to — the API returns the whole list because the CORS policy provider
   *  needs every row anyway, and an allowed-origins list is inherently short. */
  readonly labelFilter = signal('');

  readonly filteredOrigins = computed(() => {
    const term = this.labelFilter().trim().toLowerCase();
    if (!term) return this.origins();
    // A row with no label can never match a non-empty term — '—' is only how the table renders "unset".
    return this.origins().filter(o => (o.label ?? '').toLowerCase().includes(term));
  });

  readonly displayedCols = ['actions', 'originUrl', 'label', 'createdOnUtc', 'createdBy'];

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

  onLabelFilterChange(val: string): void {
    this.labelFilter.set(val);
  }

  reset(): void {
    this.labelFilter.set('');
  }

  /** Forces every running API replica to pick up the current rows right away — an admin edit already
   *  does this on its own; this is the explicit "don't want to wait" escape hatch. */
  reloadNow(): void {
    if (this.reloading()) return;
    this.reloading.set(true);
    this.svc.reload().subscribe({
      next: () => {
        this.reloading.set(false);
        // Deliberately not "reloaded on every instance" — the broadcast is fire-and-forget with no
        // delivery guarantee (see InProcessAllowedCorsOriginsCache.Invalidate), the same gap MaxAge
        // exists to cover. This only promises what's actually guaranteed.
        this.toast.success('Reload requested — every instance will pick it up within seconds, or within 5 minutes at the outside.');
      },
      error: () => {
        this.reloading.set(false);
        this.toast.error('Could not reload the CORS configuration.');
      },
    });
  }

  openAdd(): void {
    this.customDialog
      .open<AllowedCorsOriginDialogComponent, {}, boolean>(AllowedCorsOriginDialogComponent, {
        width: '480px',
        disableClose: true,
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Origin added — takes effect immediately, no restart needed.');
          this.loadOrigins();
        }
      });
  }

  openEdit(origin: AllowedCorsOrigin): void {
    this.customDialog
      .open<AllowedCorsOriginDialogComponent, { origin: AllowedCorsOrigin }, boolean>(AllowedCorsOriginDialogComponent, {
        width: '480px',
        disableClose: true,
        data: { origin },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Origin updated — takes effect immediately, no restart needed.');
          this.loadOrigins();
        }
      });
  }

  confirmDelete(origin: AllowedCorsOrigin): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
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
