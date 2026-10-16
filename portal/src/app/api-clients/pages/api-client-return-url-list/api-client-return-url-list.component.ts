import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IApiClientService } from '../../services/i-api-client.service';
import { ApiClient, ApiClientReturnUrl } from '../../models/api-client.model';
import {
  ApiClientReturnUrlDialogComponent,
  ApiClientReturnUrlDialogData,
} from '../../dialogs/api-client-return-url-dialog/api-client-return-url-dialog.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { DialogService, DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface ApiClientReturnUrlListData {
  client: ApiClient;
}

/**
 * Own screen for managing one client's Allowed Caller URL allow-list (dedicated table, not the cramped inline
 * section this used to be inside ApiClientEditDialogComponent) — opened as a full, edge-to-edge dialog
 * (fillContent + maximizable) the same way EHR Endpoints/Allowed Origins/API Clients themselves are, per
 * SettingsPageDialogService's own remarks on that pattern.
 */
@Component({
  selector: 'app-api-client-return-url-list',
  standalone: true,
  imports: [CommonModule, MatTableModule, MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './api-client-return-url-list.component.html',
  styleUrls: ['./api-client-return-url-list.component.scss'],
})
export class ApiClientReturnUrlListComponent {
  private readonly svc = inject(IApiClientService);
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  readonly dialogRef = inject<DialogRef<ApiClient>>(DialogRef);
  readonly data: ApiClientReturnUrlListData = inject(DIALOG_DATA) as ApiClientReturnUrlListData;

  // Kept live so add/edit/delete round trips update the table without closing this screen.
  readonly client = signal(this.data.client);

  readonly displayedCols = ['url', 'matchMode', 'label', 'actions'];

  openAdd(): void {
    this.customDialog
      .open<ApiClientReturnUrlDialogComponent, ApiClientReturnUrlDialogData, ApiClient>(
        ApiClientReturnUrlDialogComponent,
        { width: '520px', disableClose: true, data: { apiClientId: this.client().id } },
      )
      .afterClosed()
      .subscribe(client => {
        if (!client) return;
        this.client.set(client);
        this.toast.success('Allowed Caller URL added.');
      });
  }

  openEdit(returnUrl: ApiClientReturnUrl): void {
    this.customDialog
      .open<ApiClientReturnUrlDialogComponent, ApiClientReturnUrlDialogData, ApiClient>(
        ApiClientReturnUrlDialogComponent,
        { width: '520px', disableClose: true, data: { apiClientId: this.client().id, existing: returnUrl } },
      )
      .afterClosed()
      .subscribe(client => {
        if (!client) return;
        this.client.set(client);
        this.toast.success('Allowed Caller URL updated.');
      });
  }

  confirmDelete(returnUrl: ApiClientReturnUrl): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete Allowed Caller URL',
          message: `Remove "${returnUrl.label || returnUrl.url}" from this client's allow-list? Any browser-redirect trigger using it will start being rejected immediately.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.removeReturnUrl(this.client().id, returnUrl.id).subscribe({
          next: client => {
            this.client.set(client);
            this.toast.success('Allowed Caller URL removed.');
          },
          error: (err: HttpErrorResponse) => {
            this.toast.error(err.error?.error ?? err.error?.title ?? err.error?.message ?? 'Failed to remove return URL.');
          },
        });
      });
  }

  close(): void {
    this.dialogRef.close(this.client());
  }
}
