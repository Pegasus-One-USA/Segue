import { Component, OnInit, inject, signal, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DisableWithoutPermissionDirective } from '../../../auth/directives/disable-without-permission.directive';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { ApplicationTypeModel, SourceConnectionModel, SourceSortColumn, SortOrder } from '../../models/source-connection.model';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { WizardService } from '../../../services/wizard.service';
import { EpicAudienceFormComponent } from '../../../components/epic-source-wizard/epic-audience-form/epic-audience-form.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

@Component({
  selector: 'app-source-connection-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    DisableWithoutPermissionDirective,
    HideWithoutPermissionDirective,
    EpicAudienceFormComponent,
    PaginationBarComponent,
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
  readonly ehrFilter = signal<EhrVendor | ''>('');
  readonly audienceFilter = signal<ApplicationTypeModel | ''>('');
  readonly statusFilter = signal<'' | 'true' | 'false'>('');
  readonly sortColumn = signal<SourceSortColumn>('name');
  readonly sortDirection = signal<SortOrder>('asc');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly loading     = signal(true);

  readonly items = signal<SourceConnectionModel[]>([]);
  readonly totalCount = signal(0);
  /** Ids of connections currently referenced by at least one workflow's Source node — Edit/Delete are disabled
   *  for these so a connection a workflow still depends on can't be changed or removed out from under it. */
  readonly usedConnectionIds = signal<ReadonlySet<string>>(new Set());

  readonly displayedCols = ['index', 'name', 'sourceSystemType', 'audience', 'baseUrl', 'clientId', 'status', 'actionBy', 'actionOn', 'actions'];

  /** Only one sortable column beyond the regular ones — "Action on" — server-driven since this list is
   *  server-paged. Mutually exclusive with sortColumn/sortDirection; see onSort/toggleActionOnSort. */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
    this.pageIndex.set(0);
    this.load();
  }

  private clearActionOnSort(): void {
    this.actionOnSortDirection.set(null);
  }

  readonly ehrOptions: { value: EhrVendor; label: string }[] = [
    { value: 'Epic',               label: 'Epic' },
    { value: 'Cerner',              label: 'Cerner' },
    { value: 'GenericFhir',         label: 'Generic FHIR' },
    { value: 'Athenahealth',        label: 'Athenahealth' },
    { value: 'Allscripts',          label: 'Allscripts' },
    { value: 'Hl7v2',               label: 'HL7v2' },
    { value: 'Healow',              label: 'Healow' },
    { value: 'MeditechGreenfield',  label: 'MEDITECH Greenfield' },
    { value: 'NewEHR',              label: 'NewEHR' },
    { value: 'NewEHRTwo',           label: 'NewEHR Two' },
    { value: 'Sample',              label: 'Sample' },
  ];

  private static readonly APPLICATION_TYPE_LABELS: Record<string, string> = {
    EhrLaunch:  'Provider EHR Launch',
    Standalone: 'Provider Standalone',
    Backend:    'Backend System',
    Patient:    'Patient',
  };

  readonly audienceOptions: { value: ApplicationTypeModel; label: string }[] = [
    { value: 'Backend',    label: SourceConnectionListComponent.APPLICATION_TYPE_LABELS['Backend'] },
    { value: 'EhrLaunch',  label: SourceConnectionListComponent.APPLICATION_TYPE_LABELS['EhrLaunch'] },
    { value: 'Standalone', label: SourceConnectionListComponent.APPLICATION_TYPE_LABELS['Standalone'] },
    { value: 'Patient',    label: SourceConnectionListComponent.APPLICATION_TYPE_LABELS['Patient'] },
  ];

  /** Baseline captured once at construction so the reload effect below never fires on the component's
   *  initial render — only on a real save that happens after that baseline snapshot. */
  private readonly savedBaseline = this.wiz.saved();

  constructor() {
    effect(() => {
      const current = this.wiz.saved();
      if (current !== this.savedBaseline) this.load();
    });
  }

  ngOnInit(): void {
    this.load();
  }

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

  load(): void {
    this.loading.set(true);
    const actionOnDir = this.actionOnSortDirection();
    this.svc
      .getPaged({
        search: this.searchQuery() || undefined,
        sourceSystemType: this.ehrFilter() || undefined,
        applicationType: this.audienceFilter() || undefined,
        isEnabled: this.statusFilter() === '' ? undefined : this.statusFilter() === 'true',
        sortBy: actionOnDir ? 'actionOn' : this.sortColumn(),
        sortOrder: actionOnDir ?? this.sortDirection(),
        page: this.pageIndex() + 1,
        pageSize: this.pageSize(),
      })
      .subscribe({
        next: page => {
          this.items.set(page.items);
          this.totalCount.set(page.totalCount);
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
    this.load();
  }

  onEhrFilterChange(val: string): void {
    this.ehrFilter.set(val as EhrVendor | '');
    this.pageIndex.set(0);
    this.load();
  }

  onAudienceFilterChange(val: string): void {
    this.audienceFilter.set(val as ApplicationTypeModel | '');
    this.pageIndex.set(0);
    this.load();
  }

  onStatusFilterChange(val: string): void {
    this.statusFilter.set(val as '' | 'true' | 'false');
    this.pageIndex.set(0);
    this.load();
  }

  onSort(column: SourceSortColumn): void {
    this.clearActionOnSort();
    if (this.sortColumn() === column) {
      this.sortDirection.update(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.pageIndex.set(0);
    this.load();
  }

  reset(): void {
    this.searchQuery.set('');
    this.ehrFilter.set('');
    this.audienceFilter.set('');
    this.statusFilter.set('');
    this.sortColumn.set('name');
    this.sortDirection.set('asc');
    this.clearActionOnSort();
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
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
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to delete Source Connection.';
            this.toast.error(message);
          },
        });
      });
  }
}
