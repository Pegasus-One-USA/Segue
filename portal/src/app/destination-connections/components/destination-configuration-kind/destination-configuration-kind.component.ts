import { Component, forwardRef, inject, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { DestinationConfigurationService } from '../../services/destination-configuration.service';
import { DestinationConfigurationDto, DestinationType } from '../../models/destination-configuration.model';
import {
  DestinationConnectionDialogComponent,
  DestinationConnectionDialogData,
  destinationCreateTypeCards,
} from '../../dialogs/destination-connection-dialog/destination-connection-dialog.component';
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
import { destinationTypeLabel } from '../../../connections/connection-labels';

/**
 * Destinations (DestinationConfiguration rows) on the Destination Connections page. Edit and Delete are gated by
 * execution history: a row with pipeline run history can only be viewed, never edited or deleted — enforced here for
 * the menu and again server-side on the PUT/DELETE. Delete is also locked while a workflow's Destination node uses
 * the row. Its forms are dialogs, so it has no template of its own.
 */
@Component({
  selector: 'app-destination-configuration-kind',
  standalone: true,
  providers: [{ provide: CONNECTION_KIND_HOST, useExisting: forwardRef(() => DestinationConfigurationKindComponent) }],
  template: '',
  styleUrl: './destination-configuration-kind.component.scss',
})
export class DestinationConfigurationKindComponent implements ConnectionKindHost {
  private readonly svc = inject(DestinationConfigurationService);
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly kind = 'destination' as const;
  readonly loadErrorMessage = 'Failed to load destination connections.';

  readonly changed = output<void>();

  /** id -> hasExecutionHistory, loaded for the rows on screen so Edit/Delete are gated without a per-click trip. */
  readonly historyById = signal<Record<string, boolean>>({});
  /** ids a workflow's Destination node references, regardless of run history — gates Delete on its own. */
  readonly usedInWorkflowIds = signal<ReadonlySet<string>>(new Set());

  canList(): boolean {
    return this.permissions.hasPermission('destinationconnections.view');
  }

  list(): Observable<ConnectionRow[]> {
    this.historyById.set({});
    this.svc.getUsedInWorkflowIds().subscribe({
      next: ids => this.usedInWorkflowIds.set(new Set(ids)),
      error: () => this.usedInWorkflowIds.set(new Set()),
    });
    return this.svc.getAllPages().pipe(map(list => (Array.isArray(list) ? list : []).map(d => this.toRow(d))));
  }

  private toRow(d: DestinationConfigurationDto): ConnectionRow {
    return {
      kind: this.kind,
      key: `${this.kind}:${d.id}`,
      id: d.id,
      name: d.name,
      typeLabel: destinationTypeLabel(d.destinationType),
      filterKeys: ['kind:destination', `destination:${d.destinationType}`],
      audience: null,
      address: d.target || null,
      clientId: null,
      writeApis: null,
      isEnabled: d.isEnabled,
      actionBy: d.modifiedBy || d.createdBy || null,
      actionOn: d.modifiedOnUtc || d.createdOnUtc || null,
      raw: d,
    };
  }

  /** Loads the execution-history flag for rows now on screen that have none yet. Silent: a background flag. */
  rowsShown(rows: ConnectionRow[]): void {
    const known = this.historyById();
    const ids = rows.filter(r => r.kind === this.kind && !(r.id in known)).map(r => r.id);
    if (ids.length === 0) return;
    forkJoin(ids.map(id => this.svc.hasExecutionHistory(id, true).pipe(
      map(res => [id, res.hasExecutionHistory] as const),
      catchError(() => of([id, false] as const)),
    ))).subscribe(pairs => this.historyById.update(current => ({ ...current, ...Object.fromEntries(pairs) })));
  }

  createCards(): ConnectionKindCard[] {
    return destinationCreateTypeCards(this.permissions).map(card => ({
      kind: this.kind,
      value: card.destinationType,
      label: card.title,
      sub: card.description,
      abbr: card.title.split(/\s+/).slice(0, 2).map(word => word.charAt(0)).join('').toUpperCase(),
      color: 'var(--color-primary)',
    }));
  }

  create(card: ConnectionKindCard): boolean {
    const permissionCode = destinationCreateTypeCards(this.permissions)
      .find(c => c.destinationType === card.value)?.permissionCode;
    if (!permissionCode || !this.actionGuard.ensure(permissionCode, `You do not have permission to create a ${card.label} destination.`)) return false;
    this._openDialog({ mode: 'create', initialType: card.value as DestinationType }, 'Destination connection created.');
    return true;
  }

  /** `{destinationType}.edit`, e.g. `sqlserver.edit` — the code the backend authorizes PUT /destinations/{id}. */
  editCode(item: DestinationConfigurationDto): string {
    return `${item.destinationType.toLowerCase()}.edit`;
  }

  /** Delete requires BOTH destinationconnections.delete AND the type's own `{destinationType}.delete`
   *  (ConfigurationsController.cs checks both). */
  deleteCodes(item: DestinationConfigurationDto): string[] {
    return ['destinationconnections.delete', `${item.destinationType.toLowerCase()}.delete`];
  }

  /** A history-locked row is view-only whatever the edit right, so it can always be opened; only the editable case
   *  needs the permission check, since that is the one that lets a change through. */
  canOpenEntity(item: DestinationConfigurationDto): boolean {
    return this.hasHistory(item) || this.permissions.hasPermission(this.editCode(item));
  }

  hasHistory(item: DestinationConfigurationDto): boolean {
    return this.historyById()[item.id] ?? false;
  }

  isUsedInWorkflow(item: DestinationConfigurationDto): boolean {
    return this.usedInWorkflowIds().has(item.id);
  }

  rowActions(row: ConnectionRow): ConnectionRowAction[] {
    const d = row.raw as DestinationConfigurationDto;
    const actions: ConnectionRowAction[] = [];
    if (this.permissions.hasPermission('destinationconnections.view')) {
      // Placeholder only — viewing a destination's details is not implemented yet.
      actions.push({ id: 'view', label: 'View (Coming Soon)', icon: 'visibility', disabled: true });
    }
    if (this.canOpenEntity(d)) {
      const history = this.hasHistory(d);
      actions.push({ id: 'edit', label: history ? 'View (has execution history)' : 'Edit', icon: history ? 'visibility' : 'edit', disabled: false });
    }
    if (this.permissions.hasAll(this.deleteCodes(d))) {
      const label = this.hasHistory(d)
        ? 'Cannot delete — has execution history'
        : this.isUsedInWorkflow(d) ? 'Cannot delete — referenced by a workflow' : 'Delete';
      actions.push({ id: 'delete', label, icon: 'delete_outline', disabled: this.hasHistory(d) || this.isUsedInWorkflow(d), danger: true });
    }
    return actions;
  }

  private readonly actionHandlers: Record<ConnectionActionId, (d: DestinationConfigurationDto) => void> = {
    view: () => undefined,
    test: () => undefined,
    edit: d => this.openEdit(d),
    delete: d => this.confirmDelete(d),
  };

  runAction(id: ConnectionActionId, row: ConnectionRow): void {
    this.actionHandlers[id](row.raw as DestinationConfigurationDto);
  }

  openEdit(item: DestinationConfigurationDto): void {
    const mode = this.hasHistory(item) ? 'view' : 'edit';
    if (mode === 'edit' && !this.actionGuard.ensure(this.editCode(item), `You do not have permission to edit this ${item.destinationType} destination connection.`)) return;
    this._openDialog({ mode, destination: item }, 'Destination connection updated.');
  }

  private _openDialog(data: DestinationConnectionDialogData, successMessage: string): void {
    this.customDialog
      .open<DestinationConnectionDialogComponent, DestinationConnectionDialogData, DestinationConfigurationDto | false>(DestinationConnectionDialogComponent, {
        // Edge-to-edge, full content-area panel, so the per-type connection forms (which can run long) get the full
        // height with proper internal scrolling.
        fillContent: true,
        maximizable: true,
        disableClose: true,
        data,
      })
      .afterClosed()
      .subscribe(result => {
        if (result) {
          this.toast.success(successMessage);
          this.changed.emit();
        }
      });
  }

  confirmDelete(item: DestinationConfigurationDto): void {
    if (this.hasHistory(item) || this.isUsedInWorkflow(item)) return;
    if (!this.actionGuard.ensure(this.deleteCodes(item), 'You do not have permission to delete this destination connection.', 'all')) return;

    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
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
            this.toast.success(`"${item.name}" deleted.`);
            this.changed.emit();
          },
          error: (err: HttpErrorResponse) => {
            this.toast.error(err.error?.title ?? 'Failed to delete the destination connection.');
          },
        });
      });
  }
}
