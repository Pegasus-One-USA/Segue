import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsHandler, CdsServiceInstance, SaveCdsServiceInstanceRequest } from '../../models/cds.models';
import { cdsHookLabel } from '../../models/cds-labels';
import {
  CdsHandlerPickerDialogComponent,
  CdsHandlerPickerDialogData,
} from '../../dialogs/cds-handler-picker-dialog/cds-handler-picker-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { CdsFixesBadgeComponent } from '../../components/cds-fixes-badge/cds-fixes-badge.component';
import { cdsSavedOptions } from '../../models/cds-service-options.util';

/** The configured CDS services: one row per instance, with an on/off switch and an Add service picker. */
@Component({
  selector: 'app-cds-service-list',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatTooltipModule,
    MatSlideToggleModule,
    HideWithoutPermissionDirective,
    CdsFixesBadgeComponent,
  ],
  templateUrl: './cds-service-list.component.html',
  styleUrl: './cds-service-list.component.scss',
})
export class CdsServiceListComponent implements OnInit {
  private readonly api         = inject(CdsApiService);
  private readonly dialog      = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly router      = inject(Router);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly loading = signal(true);
  readonly services = signal<CdsServiceInstance[]>([]);
  readonly search = signal('');
  /** Ids whose enabled switch is being saved, so a second click can't race the first. */
  readonly toggling = signal<ReadonlySet<string>>(new Set());

  readonly displayedCols = ['actions', 'title', 'handler', 'hook', 'enabled', 'changed'];
  readonly hookLabel = cdsHookLabel;

  readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    if (!term) return this.services();
    return this.services().filter(s =>
      s.title.toLowerCase().includes(term)
      || s.serviceId.includes(term)
      || (s.handlerDisplayName ?? s.handlerKey).toLowerCase().includes(term));
  });

  readonly canEdit = computed(() => this.permissions.hasPermission('cdshooks.edit'));

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.getServices().subscribe({
      next: services => {
        this.services.set(services);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Could not load CDS services.');
      },
    });
  }

  openAdd(): void {
    if (!this.actionGuard.ensure('cdshooks.create', 'You do not have permission to add CDS services.')) return;
    this.api.getHandlers().subscribe({
      next: handlers => this.openPicker(handlers),
      error: () => this.toast.error('Could not load the list of checks.'),
    });
  }

  private openPicker(handlers: CdsHandler[]): void {
    this.dialog
      .open<CdsHandlerPickerDialogComponent, CdsHandlerPickerDialogData, string>(CdsHandlerPickerDialogComponent, {
        width: '620px',
        data: { handlers },
      })
      .afterClosed()
      .subscribe(handlerKey => {
        if (handlerKey) this.router.navigate(['/clinical-decision-support/services/new'], { queryParams: { handler: handlerKey } });
      });
  }

  toggleEnabled(service: CdsServiceInstance, enabled: boolean): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to edit CDS services.')) return;
    this.setToggling(service.id, true);
    const request: SaveCdsServiceInstanceRequest = {
      serviceId: service.serviceId,
      title: service.title,
      handlerKey: service.handlerKey,
      hook: service.hook,
      description: service.description,
      isEnabled: enabled,
      // The stored values only: re-sending the resolved ones would freeze today's defaults into the service.
      settings: service.storedSettings,
      cardSourceLabel: service.cardSourceLabel,
      cardSourceUrl: service.cardSourceUrl,
      allowedIssuerIds: service.allowedIssuerIds,
      // The API replaces the whole service, so fixes, reasons and links go back exactly as saved.
      ...cdsSavedOptions(service),
    };
    this.api.updateService(service.id, request).subscribe({
      next: updated => {
        this.setToggling(service.id, false);
        this.services.update(list => list.map(s => (s.id === updated.id ? updated : s)));
        this.toast.success(`"${updated.title}" is now ${updated.isEnabled ? 'on' : 'off'}.`);
      },
      error: (err: HttpErrorResponse) => {
        this.setToggling(service.id, false);
        // A fresh row object makes the table re-render that row, so the switch snaps back to the saved state.
        this.services.update(list => list.map(s => (s.id === service.id ? { ...s } : s)));
        this.toast.error(err.message || 'Could not change the service.');
      },
    });
  }

  confirmDelete(service: CdsServiceInstance): void {
    if (!this.actionGuard.ensure('cdshooks.delete', 'You do not have permission to delete CDS services.')) return;
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: 'Delete service',
          message: `Delete "${service.title}"? EHRs calling it will get "not found" from then on.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.api.deleteService(service.id).subscribe({
          next: () => {
            this.toast.success(`"${service.title}" deleted.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => this.toast.error(err.message || 'Could not delete the service.'),
        });
      });
  }

  isToggling(id: string): boolean {
    return this.toggling().has(id);
  }

  private setToggling(id: string, on: boolean): void {
    this.toggling.update(current => {
      const next = new Set(current);
      if (on) next.add(id); else next.delete(id);
      return next;
    });
  }
}
