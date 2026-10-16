import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { debounceTime, distinctUntilChanged, Subject } from 'rxjs';
import { IApiClientService } from '../../services/i-api-client.service';
import { ApiClient, ApiClientCredential } from '../../models/api-client.model';
import { ApiClientCreateDialogComponent } from '../../dialogs/api-client-create-dialog/api-client-create-dialog.component';
import { ApiClientEditDialogComponent, ApiClientEditDialogData } from '../../dialogs/api-client-edit-dialog/api-client-edit-dialog.component';
import { ApiClientSecretDialogComponent, ApiClientSecretDialogData } from '../../dialogs/api-client-secret-dialog/api-client-secret-dialog.component';
import {
  ApiClientReturnUrlListComponent,
  ApiClientReturnUrlListData,
} from '../api-client-return-url-list/api-client-return-url-list.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';

@Component({
  selector: 'app-api-client-list',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatPaginatorModule,
  ],
  templateUrl: './api-client-list.component.html',
  styleUrls: ['./api-client-list.component.scss'],
})
export class ApiClientListComponent implements OnInit {
  private readonly svc = inject(IApiClientService);
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(true);
  readonly searching = signal(false);
  readonly clients = signal<ApiClient[]>([]);

  readonly searchQuery = signal('');
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly totalCount = signal(0);

  readonly showingFrom = computed(() => this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1);
  readonly showingTo = computed(() => Math.min((this.pageIndex() + 1) * this.pageSize(), this.totalCount()));

  readonly displayedCols = ['actions', 'name', 'clientId', 'isEnabled', 'lastUsedOnUtc', 'createdOnUtc'];

  private readonly searchChanged = new Subject<string>();

  ngOnInit(): void {
    this.searchChanged.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => {
      this.pageIndex.set(0);
      this.loadClients(true);
    });

    this.loadClients();
  }

  loadClients(silent = false): void {
    this.loading.set(true);
    if (silent) this.searching.set(true);
    this.svc.getPaged({
      search: this.searchQuery().trim() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }, silent).subscribe({
      next: result => {
        this.searching.set(false);
        this.clients.set(result.items);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => {
        this.searching.set(false);
        this.loading.set(false);
        this.toast.error('Failed to load API clients.');
      },
    });
  }

  onSearchChange(val: string): void {
    this.searchQuery.set(val);
    this.searchChanged.next(val);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.loadClients();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadClients();
  }

  openCreate(): void {
    this.customDialog
      .open<ApiClientCreateDialogComponent, Record<string, never>, ApiClientCredential>(ApiClientCreateDialogComponent, {
        width: '480px',
        disableClose: true,
      })
      .afterClosed()
      .subscribe(credential => {
        if (!credential) return;
        this.loadClients();
        this.showSecret(credential);
      });
  }

  openEdit(client: ApiClient): void {
    this.customDialog
      .open<ApiClientEditDialogComponent, ApiClientEditDialogData, ApiClient>(ApiClientEditDialogComponent, {
        width: '520px',
        disableClose: true,
        data: { client },
      })
      .afterClosed()
      .subscribe(updated => {
        if (!updated) return;
        this.toast.success('API client updated.');
        this.loadClients();
      });
  }

  confirmRegenerateSecret(client: ApiClient): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Regenerate Secret',
          message: `This immediately invalidates "${client.name}"'s current Secret — every integration using it must be updated with the new one. The Client ID stays the same. Continue?`,
          confirmLabel: 'Regenerate',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.regenerateSecret(client.id).subscribe({
          next: credential => {
            this.loadClients();
            this.showSecret(credential);
          },
          error: (err: HttpErrorResponse) => {
            this.toast.error(err.error?.error ?? err.error?.title ?? err.error?.message ?? 'Failed to regenerate the secret.');
          },
        });
      });
  }

  confirmDelete(client: ApiClient): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete API Client',
          message: `Are you sure you want to delete "${client.name}"? Any integration using its credentials will immediately lose the ability to trigger workflows.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(client.id).subscribe({
          next: () => {
            this.toast.success(`"${client.name}" deleted.`);
            this.loadClients();
          },
          error: (err: HttpErrorResponse) => {
            this.toast.error(err.error?.error ?? err.error?.title ?? err.error?.message ?? 'Failed to delete API client.');
          },
        });
      });
  }

  openReturnUrls(client: ApiClient): void {
    this.customDialog
      .open<ApiClientReturnUrlListComponent, ApiClientReturnUrlListData, ApiClient>(
        ApiClientReturnUrlListComponent,
        { fillContent: true, maximizable: true, disableClose: true, data: { client } },
      )
      .afterClosed()
      .subscribe(updated => {
        if (!updated) return;
        this.loadClients();
      });
  }

  private showSecret(credential: ApiClientCredential): void {
    this.customDialog.open<ApiClientSecretDialogComponent, ApiClientSecretDialogData, void>(
      ApiClientSecretDialogComponent,
      {
        width: '560px',
        disableClose: true,
        data: { clientId: credential.client.clientId, plaintextSecret: credential.plaintextSecret },
      },
    );
  }
}
