import { Component, OnInit, inject, signal, computed, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DisableWithoutPermissionDirective } from '../../../auth/directives/disable-without-permission.directive';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { SourceConnectionModel } from '../../models/source-connection.model';
import { WizardService } from '../../../services/wizard.service';
import { EpicAudienceFormComponent } from '../../../components/epic-source-wizard/epic-audience-form/epic-audience-form.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

@Component({
  selector: 'app-source-connection-list',
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
    DisableWithoutPermissionDirective,
    HideWithoutPermissionDirective,
    EpicAudienceFormComponent,
  ],
  templateUrl: './source-connection-list.component.html',
  styleUrls: ['./source-connection-list.component.scss'],
})
export class SourceConnectionListComponent implements OnInit {
  private readonly svc    = inject(ISourceConnectionService);
  private readonly dialog = inject(MatDialog);
  private readonly toast  = inject(ToastService);
  protected readonly wiz  = inject(WizardService);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly loading     = signal(true);

  readonly connections = signal<SourceConnectionModel[]>([]);
  /** Ids of connections currently referenced by at least one workflow's Source node — Edit/Delete are disabled
   *  for these so a connection a workflow still depends on can't be changed or removed out from under it. */
  readonly usedConnectionIds = signal<ReadonlySet<string>>(new Set());

  readonly displayedCols = ['index', 'name', 'sourceSystemType', 'audience', 'baseUrl', 'clientId', 'status', 'actions'];

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    if (!q) return this.connections();
    return this.connections().filter(c =>
      c.name.toLowerCase().includes(q) ||
      c.sourceSystemType.toLowerCase().includes(q) ||
      (c.applicationType ?? '').toLowerCase().includes(q) ||
      c.baseUrl.toLowerCase().includes(q)
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

  /** Baseline captured once at construction so the reload effect below never fires on the component's
   *  initial render — only on a real save that happens after that baseline snapshot. */
  private readonly savedBaseline = this.wiz.saved();

  constructor() {
    effect(() => {
      const current = this.wiz.saved();
      if (current !== this.savedBaseline) this.loadConnections();
    });
  }

  ngOnInit(): void {
    this.loadConnections();
  }

  private static readonly APPLICATION_TYPE_LABELS: Record<string, string> = {
    EhrLaunch:  'Provider EHR Launch',
    Standalone: 'Provider Standalone',
    Backend:    'Backend System',
    Patient:    'Patient',
  };

  /** "Audience" column — the SMART application type (ApplicationType) this connection is configured under.
   *  There is no separate sandbox/production "Environment" field persisted on SourceConnection; that's a
   *  client-side wizard concept only used to pick default URLs, never saved to the backend. */
  audienceOf(c: SourceConnectionModel): string {
    return (c.applicationType && SourceConnectionListComponent.APPLICATION_TYPE_LABELS[c.applicationType]) || '—';
  }

  clientIdOf(c: SourceConnectionModel): string {
    return c.authentication?.clientId ?? '—';
  }

  isUsedInWorkflow(c: SourceConnectionModel): boolean {
    return this.usedConnectionIds().has(c.id);
  }

  loadConnections(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: connections => {
        this.connections.set(connections);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load source connections.');
      },
    });

    // Independent of the list load above — a failure here shouldn't block the grid from rendering, it just
    // leaves Edit/Delete enabled until it succeeds (fails safe toward "editable", not toward "silently blocked").
    this.svc.getUsedIds().subscribe({
      next: ids => this.usedConnectionIds.set(new Set(ids)),
      error: () => this.toast.error('Could not check workflow usage — Edit/Delete may be enabled for a connection still in use.'),
    });
  }

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

  openAdd(): void {
    this.wiz.openEntity(null);
  }

  openView(connection: SourceConnectionModel): void {
    this.wiz.openEntity(connection, { readonly: true });
  }

  openEdit(connection: SourceConnectionModel): void {
    if (this.isUsedInWorkflow(connection)) return;
    this.wiz.openEntity(connection, { readonly: false });
  }

  onModalBackdropClick(event: MouseEvent): void {
    if ((event.target as HTMLElement).classList.contains('sc-modal-backdrop')) {
      this.wiz.close();
    }
  }

  confirmDelete(connection: SourceConnectionModel): void {
    if (this.isUsedInWorkflow(connection)) return;
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Source Connection',
          message: 'Are you sure you want to delete this Source Connection?',
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(connection.id).subscribe({
          next: () => {
            this.toast.success('Source Connection deleted successfully.');
            this.loadConnections();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to delete Source Connection.';
            this.toast.error(message);
          },
        });
      });
  }
}
