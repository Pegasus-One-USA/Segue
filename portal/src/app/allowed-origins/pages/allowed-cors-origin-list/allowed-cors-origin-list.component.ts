import { Component, OnInit, HostListener, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
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
    MatIconModule,
    MatProgressSpinnerModule,
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

  /** Which row's "more actions" menu is open, if any — keyed by origin id. See toggleActionMenu,
   *  mirrored 1:1 from workflow-list.component.ts's identical row-menu pattern. */
  readonly openActionMenuId = signal<string | null>(null);

  /** Viewport-relative coordinates for the open action menu — see workflow-list.component.ts's
   *  actionMenuPosition for the full rationale (position: fixed escapes .table-wrapper's overflow-x: auto
   *  clipping). */
  readonly actionMenuPosition = signal<{ top?: number; bottom?: number; left: number } | null>(null);

  /** Rough panel height (single Remove item + padding) — just needs to be in the right ballpark to
   *  decide whether the panel fits below the trigger, not pixel-exact. */
  private static readonly ACTION_MENU_ESTIMATED_HEIGHT = 70;

  ngOnInit(): void {
    this.loadOrigins();
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
      spaceBelow < AllowedCorsOriginListComponent.ACTION_MENU_ESTIMATED_HEIGHT
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
