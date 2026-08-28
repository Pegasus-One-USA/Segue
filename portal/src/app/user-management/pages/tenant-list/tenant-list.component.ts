import { Component, HostListener, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { TenantRoleService, Tenant } from '../../services/tenant-role.service';
import { TenantDialogComponent } from '../../dialogs/tenant-dialog/tenant-dialog.component';
import { ConfirmDialogComponent } from '../../dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

@Component({
  selector: 'app-tenant-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './tenant-list.component.html',
  styleUrls: ['./tenant-list.component.scss'],
})
export class TenantListComponent {
  private readonly svc    = inject(TenantRoleService);
  private readonly dialog = inject(MatDialog);
  private readonly toast  = inject(ToastService);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);

  /** Which row's "more actions" menu is open, if any — keyed by tenant id. See toggleActionMenu,
   *  mirrored 1:1 from workflow-list.component.ts's identical row-menu pattern. */
  readonly openActionMenuId = signal<string | null>(null);

  /** Viewport-relative coordinates for the open action menu — see workflow-list.component.ts's
   *  actionMenuPosition for the full rationale (position: fixed escapes .table-wrapper's overflow-x: auto
   *  clipping). */
  readonly actionMenuPosition = signal<{ top?: number; bottom?: number; left: number } | null>(null);

  /** Rough panel height (Edit + Delete + padding) — just needs to be in the right ballpark to decide
   *  whether the panel fits below the trigger, not pixel-exact. */
  private static readonly ACTION_MENU_ESTIMATED_HEIGHT = 110;

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    if (!q) return this.svc.tenants();
    return this.svc.tenants().filter(t =>
      t.name.toLowerCase().includes(q) || t.code.toLowerCase().includes(q)
    );
  });

  readonly paginated = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  readonly showingFrom = computed(() =>
    this.filtered().length === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.filtered().length)
  );

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.pageIndex.set(0);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
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
      spaceBelow < TenantListComponent.ACTION_MENU_ESTIMATED_HEIGHT
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

  openAdd(): void {
    this.dialog
      .open(TenantDialogComponent, {
        width: '480px',
        disableClose: true,
        restoreFocus: false,
        data: {},
      })
      .afterClosed()
      .subscribe(res => {
        if (res) this.toast.success('Tenant added successfully.');
      });
  }

  openEdit(tenant: Tenant): void {
    this.dialog
      .open(TenantDialogComponent, {
        width: '480px',
        disableClose: true,
        restoreFocus: false,
        data: { tenant },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) this.toast.success('Tenant updated successfully.');
      });
  }

  confirmDelete(tenant: Tenant): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Tenant',
          message: `Are you sure you want to delete tenant "${tenant.name}"? This cannot be undone. A tenant that still has users cannot be deleted.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.deleteTenant(tenant.id).subscribe({
          next: () => this.toast.success(`Tenant "${tenant.name}" deleted.`),
          error: (err) => this.toast.error(
            'Delete failed', err?.error?.message ?? `Could not delete tenant "${tenant.name}".`),
        });
      });
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
