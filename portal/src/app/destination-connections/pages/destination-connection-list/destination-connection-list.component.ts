import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { DestinationConfigurationService } from '../../services/destination-configuration.service';
import { DestinationConfigurationDto, DestinationType } from '../../models/destination-configuration.model';
import {
  DestinationConnectionDialogComponent,
  DestinationConnectionDialogData,
} from '../../dialogs/destination-connection-dialog/destination-connection-dialog.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';

/**
 * Standalone admin CRUD for DestinationConfiguration rows — server-side paged/filtered (no existing screen in
 * the portal does this today; every other list fetches everything and paginates client-side). Edit/Delete are
 * gated by execution history: a row with pipeline run history can only be viewed, never edited or deleted,
 * enforced here for the buttons and again server-side (the actual source of truth) on the PUT/DELETE calls.
 */
@Component({
  selector: 'app-destination-connection-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './destination-connection-list.component.html',
  styleUrls: ['./destination-connection-list.component.scss'],
})
export class DestinationConnectionListComponent implements OnInit {
  private readonly svc = inject(DestinationConfigurationService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly searchQuery = signal('');
  readonly typeFilter = signal<DestinationType | ''>('');
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly loading = signal(true);

  readonly items = signal<DestinationConfigurationDto[]>([]);
  readonly totalCount = signal(0);
  /** id -> hasExecutionHistory, loaded alongside each page so Edit/Delete can be gated without a per-click round trip. */
  readonly historyById = signal<Record<string, boolean>>({});
  /** ids currently referenced by at least one workflow's Destination node, regardless of run history — gates
   *  Delete independently of historyById (a never-run destination can still be wired into a live workflow). */
  readonly usedInWorkflowIds = signal<Set<string>>(new Set());

  readonly displayedCols = ['name', 'destinationType', 'target', 'isEnabled', 'actions'];

  readonly typeOptions: { value: DestinationType; label: string }[] = [
    { value: 'SqlServer', label: 'SQL Server' },
    { value: 'Csv', label: 'CSV' },
    { value: 'Sftp', label: 'CSV (SFTP)' },
  ];

  readonly showingFrom = computed(() =>
    this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );
  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.totalCount())
  );

  ngOnInit(): void {
    this.load();
    this._loadUsedInWorkflowIds();
  }

  private _loadUsedInWorkflowIds(): void {
    this.svc.getUsedInWorkflowIds().subscribe({
      next: ids => this.usedInWorkflowIds.set(new Set(ids)),
      error: () => this.usedInWorkflowIds.set(new Set()),
    });
  }

  isUsedInWorkflow(item: DestinationConfigurationDto): boolean {
    return this.usedInWorkflowIds().has(item.id);
  }

  load(): void {
    this.loading.set(true);
    this.svc
      .getPaged({
        search: this.searchQuery() || undefined,
        destinationType: this.typeFilter() || undefined,
        page: this.pageIndex() + 1,
        pageSize: this.pageSize(),
      })
      .subscribe({
        next: page => {
          this.items.set(page.items);
          this.totalCount.set(page.totalCount);
          this._loadHistoryFlags(page.items);
          this.loading.set(false);
        },
        error: () => {
          this.loading.set(false);
          this.snack.open('Failed to load destination connections.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  private _loadHistoryFlags(items: DestinationConfigurationDto[]): void {
    if (items.length === 0) {
      this.historyById.set({});
      return;
    }
    forkJoin(
      items.map(item =>
        this.svc.hasExecutionHistory(item.id).pipe(
          map(res => [item.id, res.hasExecutionHistory] as const),
          catchError(() => of([item.id, false] as const)),
        ),
      ),
    ).subscribe(pairs => this.historyById.set(Object.fromEntries(pairs)));
  }

  hasHistory(item: DestinationConfigurationDto): boolean {
    return this.historyById()[item.id] ?? false;
  }

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.pageIndex.set(0);
    this.load();
  }

  onTypeFilterChange(val: string): void {
    this.typeFilter.set(val as DestinationType | '');
    this.pageIndex.set(0);
    this.load();
  }

  reset(): void {
    this.searchQuery.set('');
    this.typeFilter.set('');
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  openNew(): void {
    this._openDialog({ mode: 'create' }, 'Destination connection created.');
  }

  openEdit(item: DestinationConfigurationDto): void {
    const mode = this.hasHistory(item) ? 'view' : 'edit';
    this._openDialog({ mode, destination: item }, 'Destination connection updated.');
  }

  private _openDialog(data: DestinationConnectionDialogData, successMessage: string): void {
    this.dialog
      .open(DestinationConnectionDialogComponent, {
        width: '640px',
        disableClose: true,
        restoreFocus: false,
        data,
      })
      .afterClosed()
      .subscribe(result => {
        if (result) {
          this.snack.open(successMessage, 'Dismiss', { duration: 3000 });
          this.load();
        }
      });
  }

  confirmDelete(item: DestinationConfigurationDto): void {
    if (this.hasHistory(item) || this.isUsedInWorkflow(item)) return;

    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Destination Connection',
          message: `Are you sure you want to delete "${item.name}"? This action cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(item.id).subscribe({
          next: () => {
            this.snack.open(`"${item.name}" deleted.`, 'Dismiss', { duration: 3000 });
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to delete the destination connection.';
            this.snack.open(message, 'Dismiss', { duration: 5000 });
          },
        });
      });
  }
}
