import { Component, OnInit, OnDestroy, inject, signal, effect, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { DialogService } from '../../../core/services/dialog.service';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { ApplicationTypeModel, SourceConnectionModel, SourceSortColumn, SortOrder } from '../../models/source-connection.model';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { WizardService } from '../../../services/wizard.service';
import { PhaseConfigService } from '../../../services/phase-config.service';
import { SOURCES } from '../../../data/sources.data';
import { EHR_VENDOR_TO_SOURCE_FORM_KEY, SELF_CONTAINED_SOURCE_FORM_KEYS } from '../../../components/node-library/source-form.registry';
import { EpicSourceFormComponent } from '../../../components/node-library/epic-source-form/epic-source-form.component';
import { CernerSourceFormComponent } from '../../../components/node-library/cerner-source-form/cerner-source-form.component';
import { AthenahealthSourceFormComponent } from '../../../components/node-library/athenahealth-source-form/athenahealth-source-form.component';
import { AllscriptsSourceFormComponent } from '../../../components/node-library/allscripts-source-form/allscripts-source-form.component';
import { HealowSourceFormComponent } from '../../../components/node-library/healow-source-form/healow-source-form.component';
import { MeditechSourceFormComponent } from '../../../components/node-library/meditech-source-form/meditech-source-form.component';
import { SampleSourceFormComponent } from '../../../components/node-library/sample-source-form/sample-source-form.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

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
    MatMenuModule,
    HideWithoutPermissionDirective,
    EpicSourceFormComponent,
    CernerSourceFormComponent,
    AthenahealthSourceFormComponent,
    AllscriptsSourceFormComponent,
    HealowSourceFormComponent,
    MeditechSourceFormComponent,
    SampleSourceFormComponent,
    PaginationBarComponent,
  ],
  templateUrl: './source-connection-list.component.html',
  styleUrls: ['./source-connection-list.component.scss'],
})
export class SourceConnectionListComponent implements OnInit, OnDestroy {
  private readonly svc         = inject(ISourceConnectionService);
  private readonly customDialog = inject(DialogService);
  private readonly toast       = inject(ToastService);
  protected readonly wiz       = inject(WizardService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly phaseCfg    = inject(PhaseConfigService);

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
  /** Whether the open per-vendor connection form is expanded to fill the whole browser window
   *  (sidebar/topbar/footer included), same "maximize" concept as the workflow builder's node config
   *  panel — see the vendor forms' own `isMaximized`/`toggleMaximizeRequest` and .sc-modal-panel--maximized. */
  readonly formMaximized = signal(false);
  /** Ids of connections currently referenced by at least one workflow's Source node — Edit/Delete are disabled
   *  for these so a connection a workflow still depends on can't be changed or removed out from under it. */
  readonly usedConnectionIds = signal<ReadonlySet<string>>(new Set());

  readonly displayedCols = ['actions', 'name', 'sourceSystemType', 'audience', 'baseUrl', 'clientId', 'status', 'actionBy', 'actionOn'];

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

  /** Reverse of EHR_VENDOR_TO_SOURCE_FORM_KEY — SOURCES catalog id -> backend EhrVendor (SourceSystemType) name. */
  private static readonly SOURCE_KEY_TO_EHR_VENDOR: Record<string, EhrVendor> = Object.fromEntries(
    Object.entries(EHR_VENDOR_TO_SOURCE_FORM_KEY).map(([vendor, key]) => [key, vendor as EhrVendor]),
  );

  /** Loaded exactly like the workflow canvas's source node palette (see NodeLibraryDialogComponent.allCategories):
   *  every SOURCES catalog entry, filtered by the active phase config (phaseCfg.isSourceEnabled) and by the
   *  current role's `{vendor}.view` permission. Replaces the old hand-maintained array, which had drifted out of
   *  sync with SOURCES (it still listed NewEHR/NewEHRTwo, neither of which exists there). */
  readonly ehrOptions = computed(() =>
    SOURCES
      .filter(s => this.phaseCfg.isSourceEnabled(s.id) && (!s.permissionPrefix || this.permissions.hasPermission(`${s.permissionPrefix}.view`)))
      .map(s => ({ value: SourceConnectionListComponent.SOURCE_KEY_TO_EHR_VENDOR[s.id], label: s.name }))
      .filter((o): o is { value: EhrVendor; label: string } => !!o.value)
  );

  /** Vendors offered when creating a brand-new Source Connection — narrower than ehrOptions (the row-list
   *  filter) above: only vendors with their own dedicated, WizardService-backed entity-mode form (see
   *  EHR_VENDOR_TO_SOURCE_FORM_KEY / SELF_CONTAINED_SOURCE_FORM_KEYS in node-library). GenericFhir/Hl7v2 have no
   *  entity-mode-capable form of their own (they're canvas-only, "headless" getFields() forms — see
   *  node-library-dialog.component.ts) — offering them here would silently create nothing on Save. Replaces the
   *  in-form EHR `<select>` this page used to delegate vendor choice to before that control was removed from the
   *  shared engine (see ehr-vendor-source-form.component.ts).
   */
  readonly createVendorOptions = computed(() =>
    this.ehrOptions().filter(o => o.value in EHR_VENDOR_TO_SOURCE_FORM_KEY && o.value !== 'GenericFhir' && o.value !== 'Hl7v2')
  );

  // There is no `sourceconnections.create` permission (RbacSeedData deliberately doesn't seed one —
  // Create is authorized per-vendor, e.g. `epic.create`, `cerner.create`). So "can this role create a
  // Source Connection at all" is "does it hold ANY vendor's own .create code" — narrowed to the vendors
  // createVendorOptions already offers (the ones with a real entity-mode form), so a permission the
  // user holds for a vendor with no form here (e.g. a future addition) never produces a dead option.
  readonly permittedCreateVendorOptions = computed(() =>
    this.createVendorOptions().filter(o => this.permissions.hasPermission(`${o.value.toLowerCase()}.create`))
  );

  readonly addVendor = signal<EhrVendor>(this.permittedCreateVendorOptions()[0]?.value ?? 'Epic');

  /** `{vendor}.edit` for a specific row, e.g. `epic.edit` — the code the backend actually authorizes
   *  PUT /source-connections/{id} against (ConfigurationsController.cs), never the generic
   *  `sourceconnections.edit` fallback (that only covers a vendor with no dedicated permission group). */
  vendorEditCode(c: SourceConnectionModel): string {
    return `${c.sourceSystemType.toLowerCase()}.edit`;
  }

  /** Delete requires BOTH the generic sourceconnections.delete AND the row's own vendor-specific
   *  `{vendor}.delete` — SourceConnectionsController.cs's DELETE action checks both independently
   *  ([StandardPermission(SourceConnections, Delete)] automatically, plus an explicit
   *  AuthorizePermissionAsync(sourceConnection.SourceSystemType, Delete) in the method body).
   *  Mirrored here with mode:'all' so a role missing either one doesn't see a Delete button the
   *  backend would reject. */
  deleteCodes(c: SourceConnectionModel): string[] {
    return ['sourceconnections.delete', `${c.sourceSystemType.toLowerCase()}.delete`];
  }

  /** Whether this row's 3-dot menu has anything in it at all — a view-only role (e.g. Audit) with
   *  none of View/Edit/Delete on this row should never see an empty kebab menu. */
  hasRowMenu(c: SourceConnectionModel): boolean {
    return this.permissions.hasPermission('sourceconnections.view')
      || this.permissions.hasPermission(this.vendorEditCode(c))
      || this.permissions.hasAll(this.deleteCodes(c));
  }

  /** Which registered per-vendor form to render for the currently-open entity-mode dialog — driven by
   *  wiz.ehrType() (seeded from the row being viewed/edited, or from addVendor() for a brand-new connection; see
   *  openAdd()). Falls back to 'epic' for a legacy row persisted under a vendor with no dedicated entity-mode
   *  form (GenericFhir/Hl7v2/NewEHR/NewEHRTwo) — same "Epic form regardless of vendor" behavior this page always
   *  had before the per-vendor split, now scoped to just that legacy fallback case. */
  readonly currentEntityFormKey = computed(() => {
    const key = EHR_VENDOR_TO_SOURCE_FORM_KEY[this.wiz.ehrType()];
    return key && SELF_CONTAINED_SOURCE_FORM_KEYS.has(key) ? key : 'epic';
  });

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

    // Reset once the form closes so the next one opens un-maximized rather than inheriting whatever
    // state the previous connection's form was left in.
    effect(() => {
      if (!this.wiz.isOpen()) this.formMaximized.set(false);
    });
  }

  ngOnInit(): void {
    this.load();
  }

  // WizardService is a root singleton (providedIn: 'root'), so its isOpen/wizardMode state outlives
  // this component — the template gates the entity-mode wizard purely on wiz.isOpen() (see the
  // component's own template, around the @if (wiz.isOpen() && wiz.wizardMode() === 'entity') block).
  // Without this, navigating away from this page mid-wizard (e.g. via the sidebar, without Cancel/×)
  // leaves isOpen true; returning here later re-mounts this component fresh, but the singleton still
  // says "open", so the wizard pops back up immediately instead of the list.
  ngOnDestroy(): void {
    this.wiz.close();
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

  /** Opens the "Create" flow for a brand-new Source Connection under the given vendor (see the vendor `<select>`
   *  next to "New Source Connection" in the template, bound to addVendor). Replaces this page's old reliance on
   *  the in-form EHR `<select>` — openEntity(null) itself always seeds wiz.ehrType to 'Epic', so it's set again
   *  right after, and the entity-mode-CREATE branch of EhrVendorSourceFormComponent's own vendor-sync effect
   *  keeps it there once the matching wrapper (see currentEntityFormKey) mounts. */
  openAdd(vendor: EhrVendor = this.addVendor()): void {
    if (!this.actionGuard.ensure(`${vendor.toLowerCase()}.create`, `You do not have permission to add a new ${vendor} source connection.`)) return;
    this.wiz.openEntity(null);
    this.wiz.ehrType.set(vendor);
  }

  openView(connection: SourceConnectionModel): void {
    this.wiz.openEntity(connection, { readonly: true });
  }

  openEdit(connection: SourceConnectionModel): void {
    if (this.isUsedInWorkflow(connection)) return;
    if (!this.actionGuard.ensure(this.vendorEditCode(connection), `You do not have permission to edit this ${connection.sourceSystemType} source connection.`)) return;
    this.wiz.openEntity(connection, { readonly: false });
  }

  onModalBackdropClick(event: MouseEvent): void {
    if ((event.target as HTMLElement).classList.contains('sc-modal-backdrop')) {
      this.wiz.close();
    }
  }

  confirmDelete(connection: SourceConnectionModel): void {
    if (this.isUsedInWorkflow(connection)) return;
    if (!this.actionGuard.ensure(this.deleteCodes(connection), `You do not have permission to delete this ${connection.sourceSystemType} source connection.`, 'all')) return;
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
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
