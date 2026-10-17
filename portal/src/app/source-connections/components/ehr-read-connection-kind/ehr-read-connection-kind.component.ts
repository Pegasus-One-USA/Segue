import { Component, OnDestroy, computed, effect, forwardRef, inject, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { SourceConnectionModel } from '../../models/source-connection.model';
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
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import {
  CONNECTION_KIND_HOST,
  ConnectionActionId,
  ConnectionKindCard,
  ConnectionKindHost,
  ConnectionRow,
  ConnectionRowAction,
} from '../../../connections/connection-row.model';
import { audienceLabel, ehrVendorLabel } from '../../../connections/connection-labels';
import { sourceConnectionDeleteCodes, sourceConnectionVendorEditCode } from '../../../connections/connection-permissions';

/**
 * EHR / FHIR read connections on the Source Connections page: SourceConnection rows that can read (Read, or Read &
 * Write — those are also listed on Destination Connections, as one record). Owns the per-vendor entity form (opened
 * for New, View and Edit) and the in-use locks: Edit is locked while a workflow's Source node reads the connection;
 * Delete also while an EHR write-back writes through it.
 */
@Component({
  selector: 'app-ehr-read-connection-kind',
  standalone: true,
  imports: [
    EpicSourceFormComponent,
    CernerSourceFormComponent,
    AthenahealthSourceFormComponent,
    AllscriptsSourceFormComponent,
    HealowSourceFormComponent,
    MeditechSourceFormComponent,
    SampleSourceFormComponent,
  ],
  providers: [{ provide: CONNECTION_KIND_HOST, useExisting: forwardRef(() => EhrReadConnectionKindComponent) }],
  templateUrl: './ehr-read-connection-kind.component.html',
  styleUrl: './ehr-read-connection-kind.component.scss',
})
export class EhrReadConnectionKindComponent implements ConnectionKindHost, OnDestroy {
  private readonly svc = inject(ISourceConnectionService);
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  protected readonly wiz = inject(WizardService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly phaseCfg = inject(PhaseConfigService);

  readonly kind = 'ehr-read' as const;
  readonly loadErrorMessage = 'Failed to load source connections.';

  /** A connection was saved or deleted: the page reloads this kind. */
  readonly changed = output<void>();

  /** Whether the open per-vendor form is expanded to fill the whole browser window — see the vendor forms' own
   *  `isMaximized`/`toggleMaximizeRequest` and .sc-modal-panel--maximized. */
  readonly formMaximized = signal(false);
  /** Connections a workflow's Source node reads — Edit and Delete are locked for these. */
  readonly usedReadIds = signal<ReadonlySet<string>>(new Set());
  /** Connections an EHR write-back writes through (a Read & Write row) — Delete is locked for these too. */
  readonly usedWriteIds = signal<ReadonlySet<string>>(new Set());

  /** Reverse of EHR_VENDOR_TO_SOURCE_FORM_KEY — SOURCES catalog id -> backend EhrVendor (SourceSystemType) name. */
  private static readonly SOURCE_KEY_TO_EHR_VENDOR: Record<string, EhrVendor> = Object.fromEntries(
    Object.entries(EHR_VENDOR_TO_SOURCE_FORM_KEY).map(([vendor, key]) => [key, vendor as EhrVendor]),
  );

  /** Loaded exactly like the workflow canvas's source node palette (see NodeLibraryDialogComponent.allCategories):
   *  every SOURCES catalog entry, filtered by the active phase config and by the role's `{vendor}.view`. The page's
   *  EHR filter offers these. */
  readonly ehrOptions = computed(() =>
    SOURCES
      .filter(s => this.phaseCfg.isSourceEnabled(s.id) && (!s.permissionPrefix || this.permissions.hasPermission(`${s.permissionPrefix}.view`)))
      .map(s => EhrReadConnectionKindComponent.SOURCE_KEY_TO_EHR_VENDOR[s.id])
      .filter((vendor): vendor is EhrVendor => !!vendor)
      // Same label the Type column shows, so a filter option and the rows it keeps read the same.
      .map(vendor => ({ value: vendor, label: ehrVendorLabel(vendor) })),
  );

  /** Vendors offered when creating a brand-new connection — narrower than ehrOptions: only vendors with their own
   *  dedicated, WizardService-backed entity-mode form. GenericFhir/Hl7v2 have no entity-mode-capable form of their
   *  own (they're canvas-only, "headless" getFields() forms), so offering them here would silently create nothing. */
  readonly createVendorOptions = computed(() =>
    this.ehrOptions().filter(o => o.value in EHR_VENDOR_TO_SOURCE_FORM_KEY && o.value !== 'GenericFhir' && o.value !== 'Hl7v2'),
  );

  // There is no `sourceconnections.create` permission — Create is authorized per-vendor (`epic.create`, ...). So the
  // role may create a connection for the vendors whose own .create it holds, narrowed to the ones with a real form.
  readonly permittedCreateVendorOptions = computed(() =>
    this.createVendorOptions().filter(o => this.permissions.hasPermission(`${o.value.toLowerCase()}.create`)),
  );

  /** The permitted vendors, enriched from the SOURCES catalog (abbreviation, brand colour, one-line description). */
  readonly createVendorCards = computed<ConnectionKindCard[]>(() =>
    this.permittedCreateVendorOptions().map(option => {
      const entry = SOURCES.find(s => EhrReadConnectionKindComponent.SOURCE_KEY_TO_EHR_VENDOR[s.id] === option.value);
      return {
        kind: this.kind,
        value: option.value,
        label: entry?.name ?? option.label,
        sub: entry?.sub ?? '',
        abbr: entry?.abbr ?? option.label.slice(0, 2).toUpperCase(),
        color: entry?.color ?? 'var(--color-primary)',
      };
    }),
  );

  /** Which per-vendor form the open entity session renders — wiz.ehrType() mapped through the registry. Falls back to
   *  'epic' for a legacy row under a vendor with no dedicated entity-mode form (GenericFhir/Hl7v2/...). */
  readonly currentEntityFormKey = computed(() => {
    const key = EHR_VENDOR_TO_SOURCE_FORM_KEY[this.wiz.ehrType()];
    return key && SELF_CONTAINED_SOURCE_FORM_KEYS.has(key) ? key : 'epic';
  });

  /** WizardService is a root singleton shared with the Destination Connections write editor: this kind's modal is a
   *  read-purpose entity session only. */
  readonly formOpen = computed(
    () => this.wiz.isOpen() && this.wiz.wizardMode() === 'entity' && this.wiz.purpose() === 'read',
  );

  /** Baseline captured once so the effect below fires only on a save made after this component was created. */
  private readonly savedBaseline = this.wiz.saved();

  constructor() {
    effect(() => {
      if (this.wiz.saved() !== this.savedBaseline) this.changed.emit();
    });
    // Reset once the form closes so the next one opens un-maximized.
    effect(() => {
      if (!this.wiz.isOpen()) this.formMaximized.set(false);
    });
  }

  // WizardService outlives this component: leaving the page mid-wizard must not leave the session open, or the form
  // pops straight back up the next time the page mounts.
  ngOnDestroy(): void {
    this.wiz.close();
  }

  canList(): boolean {
    return this.permissions.hasPermission('sourceconnections.view');
  }

  list(): Observable<ConnectionRow[]> {
    // Independent of the list itself: a failure only leaves Edit/Delete enabled (fails safe toward "editable"). Both
    // usage endpoints are UnifiedAdmin-only, so a non-admin role always gets 401/403 — expected, not worth a toast.
    // The server's DELETE has no usage check of its own, so these locks are a list-screen guard only.
    let usageToastShown = false;
    const onUsageError = (err: HttpErrorResponse) => {
      if (err.status === 401 || err.status === 403 || usageToastShown) return;
      usageToastShown = true;
      this.toast.error('Could not check workflow usage — Edit/Delete may be enabled for a connection still in use.');
    };
    this.svc.getUsedIds().subscribe({ next: ids => this.usedReadIds.set(new Set(ids)), error: onUsageError });
    this.svc.getWriteUsedIds().subscribe({ next: ids => this.usedWriteIds.set(new Set(ids)), error: onUsageError });

    return this.svc.getAll('read').pipe(map(list => (Array.isArray(list) ? list : []).map(c => this.toRow(c))));
  }

  private toRow(c: SourceConnectionModel): ConnectionRow {
    const keys = ['kind:ehr-read', `vendor:${c.sourceSystemType}`];
    if (c.applicationType) keys.push(`audience:${c.applicationType}`);
    return {
      kind: this.kind,
      key: `${this.kind}:${c.id}`,
      id: c.id,
      name: c.name,
      typeLabel: ehrVendorLabel(c.sourceSystemType),
      filterKeys: keys,
      audience: audienceLabel(c.applicationType),
      address: c.baseUrl || null,
      clientId: c.authentication?.clientId ?? null,
      writeApis: null,
      isEnabled: c.isEnabled,
      actionBy: c.modifiedBy || c.createdBy || null,
      actionOn: c.modifiedOnUtc || c.createdOnUtc || null,
      raw: c,
    };
  }

  createCards(): ConnectionKindCard[] {
    return this.createVendorCards();
  }

  /** openEntity(null) always seeds wiz.ehrType to 'Epic', so the chosen vendor is set right after; the entity-mode
   *  CREATE branch of EhrVendorSourceFormComponent's vendor-sync effect keeps it there once the form mounts. */
  create(card: ConnectionKindCard): boolean {
    const vendor = card.value as EhrVendor;
    if (!this.actionGuard.ensure(`${vendor.toLowerCase()}.create`, `You do not have permission to add a new ${vendor} source connection.`)) return false;
    this.wiz.openEntity(null);
    this.wiz.ehrType.set(vendor);
    return this.wiz.isOpen();
  }

  private isUsedForRead(c: SourceConnectionModel): boolean {
    return this.usedReadIds().has(c.id);
  }

  private isUsedAnywhere(c: SourceConnectionModel): boolean {
    return this.isUsedForRead(c) || this.usedWriteIds().has(c.id);
  }

  rowActions(row: ConnectionRow): ConnectionRowAction[] {
    const c = row.raw as SourceConnectionModel;
    const actions: ConnectionRowAction[] = [];
    if (this.permissions.hasPermission('sourceconnections.view')) {
      actions.push({ id: 'view', label: 'View', icon: 'visibility', disabled: false });
    }
    if (this.permissions.hasPermission(sourceConnectionVendorEditCode(c))) {
      // A read-purpose save never touches the write settings, so only read usage locks Edit.
      const used = this.isUsedForRead(c);
      actions.push({ id: 'edit', label: used ? 'Edit (used by a workflow)' : 'Edit', icon: 'edit', disabled: used });
    }
    if (this.permissions.hasAll(sourceConnectionDeleteCodes(c))) {
      const used = this.isUsedAnywhere(c);
      actions.push({ id: 'delete', label: used ? 'Delete (used by a workflow)' : 'Delete', icon: 'delete_outline', disabled: used, danger: true });
    }
    return actions;
  }

  private readonly actionHandlers: Record<ConnectionActionId, (c: SourceConnectionModel) => void> = {
    view: c => this.wiz.openEntity(c, { readonly: true }),
    edit: c => this.openEdit(c),
    test: () => undefined,
    delete: c => this.confirmDelete(c),
  };

  runAction(id: ConnectionActionId, row: ConnectionRow): void {
    this.actionHandlers[id](row.raw as SourceConnectionModel);
  }

  openEdit(c: SourceConnectionModel): void {
    if (this.isUsedForRead(c)) return;
    if (!this.actionGuard.ensure(sourceConnectionVendorEditCode(c), `You do not have permission to edit this ${c.sourceSystemType} source connection.`)) return;
    this.wiz.openEntity(c, { readonly: false });
  }

  confirmDelete(c: SourceConnectionModel): void {
    if (this.isUsedAnywhere(c)) return;
    if (!this.actionGuard.ensure(sourceConnectionDeleteCodes(c), `You do not have permission to delete this ${c.sourceSystemType} source connection.`, 'all')) return;
    const writes = c.access === 'Write' || c.access === 'ReadWrite';
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete Source Connection',
          message: 'Are you sure you want to delete this Source Connection?'
            + (writes ? ' This connection also writes to the EHR. Deleting it removes it from Destination Connections too.' : ''),
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(c.id).subscribe({
          next: () => {
            this.toast.success('Source Connection deleted successfully.');
            this.changed.emit();
          },
          error: (err: HttpErrorResponse) => {
            this.toast.error(err.error?.title ?? 'Failed to delete Source Connection.');
          },
        });
      });
  }

  onModalBackdropClick(event: MouseEvent): void {
    if ((event.target as HTMLElement).classList.contains('sc-modal-backdrop')) this.wiz.close();
  }
}
