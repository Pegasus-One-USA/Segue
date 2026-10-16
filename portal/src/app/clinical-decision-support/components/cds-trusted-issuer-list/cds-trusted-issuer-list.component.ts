import { Component, inject, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsTrustedIssuer } from '../../models/cds.models';
import {
  CdsTrustedIssuerDialogComponent,
  CdsTrustedIssuerDialogData,
} from '../../dialogs/cds-trusted-issuer-dialog/cds-trusted-issuer-dialog.component';
import {
  CdsCallbackTestDialogComponent,
  CdsCallbackTestDialogData,
} from '../../dialogs/cds-callback-test-dialog/cds-callback-test-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';

/** Trusted issuers: the EHRs whose signed calls are accepted. Add, edit and delete open from here. */
@Component({
  selector: 'app-cds-trusted-issuer-list',
  standalone: true,
  imports: [DatePipe, MatIconModule, MatButtonModule, MatMenuModule, MatTooltipModule, HideWithoutPermissionDirective],
  templateUrl: './cds-trusted-issuer-list.component.html',
  styleUrl: './cds-trusted-issuer-list.component.scss',
})
export class CdsTrustedIssuerListComponent {
  private readonly api         = inject(CdsApiService);
  private readonly dialog      = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly permissions = inject(PermissionService);

  readonly issuers = input.required<CdsTrustedIssuer[]>();
  /** Fired after any add, edit or delete so the page reloads. */
  readonly changed = output<void>();

  hasRowMenu(): boolean {
    return this.permissions.hasPermission('cdshooks.edit')
      || this.permissions.hasPermission('cdshooks.delete')
      || this.permissions.hasPermission('cdshooks.test');
  }

  openCallbackTest(issuer: CdsTrustedIssuer): void {
    if (!this.actionGuard.ensure('cdshooks.test', 'You do not have permission to test CDS connections.')) return;
    this.dialog.open<CdsCallbackTestDialogComponent, CdsCallbackTestDialogData, void>(CdsCallbackTestDialogComponent, {
      width: '560px',
      data: { issuer },
    });
  }

  openAdd(sandbox = false): void {
    if (!this.actionGuard.ensure('cdshooks.create', 'You do not have permission to add trusted issuers.')) return;
    this.openDialog({ sandbox }, 'added');
  }

  openEdit(issuer: CdsTrustedIssuer): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to edit trusted issuers.')) return;
    this.openDialog({ issuer }, 'saved');
  }

  confirmDelete(issuer: CdsTrustedIssuer): void {
    if (!this.actionGuard.ensure('cdshooks.delete', 'You do not have permission to delete trusted issuers.')) return;
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: 'Delete trusted issuer',
          message: `Delete "${issuer.name}"? Calls signed by ${issuer.issuer} will be refused from then on.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.api.deleteTrustedIssuer(issuer.id).subscribe({
          next: () => {
            this.toast.success(`"${issuer.name}" deleted.`);
            this.changed.emit();
          },
          // A 400 here names the services whose allow-list still holds this issuer.
          error: (err: HttpErrorResponse) => this.toast.error(err.message || 'Could not delete the trusted issuer.'),
        });
      });
  }

  keySourceLabel(issuer: CdsTrustedIssuer): string {
    if (issuer.jwksUrl && issuer.jwksJson) return 'URL and pasted keys';
    return issuer.jwksUrl ? 'JWKS URL' : 'Pasted keys';
  }

  private openDialog(data: CdsTrustedIssuerDialogData, verb: string): void {
    this.dialog
      .open<CdsTrustedIssuerDialogComponent, CdsTrustedIssuerDialogData, CdsTrustedIssuer>(CdsTrustedIssuerDialogComponent, {
        width: '640px',
        disableClose: true,
        data,
      })
      .afterClosed()
      .subscribe(saved => {
        if (!saved) return;
        this.toast.success(`"${saved.name}" ${verb}.`);
        this.changed.emit();
      });
  }
}
