import { Component, forwardRef, inject, output, signal, viewChild } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
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
import { writeVendorLabel } from '../../../connections/connection-labels';
import {
  creatableWriteVendorCards,
  sourceConnectionDeleteCodes,
  toConnectionKindCard,
  writeConnectionCreateCodes,
} from '../../../connections/connection-permissions';
import { writeApisLabel } from '../../../connections/ehr-write-vendors';
import { EhrWriteConnectionEditorComponent } from '../ehr-write-connection-editor/ehr-write-connection-editor.component';

/**
 * EHR write connections on the Destination Connections page: the connections an EHR Write-Back destination writes
 * through. They are SourceConnection rows with write access (Write, or Read & Write — those are also listed on
 * Source Connections, as one record). Listed with ehrwriteback.view plus sourceconnections.view (the list comes from
 * GET source-connections, which the server gates on sourceconnections.view). New needs ehrwriteback.create plus the
 * vendor's create right; Edit needs ehrwriteback.edit plus the vendor's edit right (the server requires
 * ehrwriteback.edit for any change to the write side of a connection that can write).
 */
@Component({
  selector: 'app-ehr-write-connection-kind',
  standalone: true,
  imports: [EhrWriteConnectionEditorComponent],
  providers: [{ provide: CONNECTION_KIND_HOST, useExisting: forwardRef(() => EhrWriteConnectionKindComponent) }],
  templateUrl: './ehr-write-connection-kind.component.html',
  styleUrl: './ehr-write-connection-kind.component.scss',
})
export class EhrWriteConnectionKindComponent implements ConnectionKindHost {
  private readonly svc = inject(ISourceConnectionService);
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly editor = viewChild.required(EhrWriteConnectionEditorComponent);

  readonly kind = 'ehr-write' as const;
  readonly loadErrorMessage = 'Loading EHR write connections failed. Try again.';

  readonly changed = output<void>();

  /** Connections an EHR write-back writes through — Delete is locked for these. */
  readonly usedWriteIds = signal<ReadonlySet<string>>(new Set());
  /** Connections a workflow's Source node reads — Delete is locked for a Read & Write row among these. */
  readonly usedReadIds = signal<ReadonlySet<string>>(new Set());

  /** Both rights: the write-back right, and the source-connections right the list endpoint checks. */
  canList(): boolean {
    return this.permissions.hasAll(['ehrwriteback.view', 'sourceconnections.view']);
  }

  list(): Observable<ConnectionRow[]> {
    // Silent: both are UnifiedAdmin-only, so a non-admin role always gets 401/403, and any failure only leaves Delete
    // unlocked (the list stays usable). The server's DELETE has no usage check, so this is a list-screen guard only.
    const silent = () => undefined;
    this.svc.getWriteUsedIds().subscribe({ next: ids => this.usedWriteIds.set(new Set(ids)), error: silent });
    this.svc.getUsedIds().subscribe({ next: ids => this.usedReadIds.set(new Set(ids)), error: silent });
    return this.svc.getAll('write').pipe(map(list => (Array.isArray(list) ? list : []).map(c => this.toRow(c))));
  }

  private toRow(c: SourceConnectionModel): ConnectionRow {
    return {
      kind: this.kind,
      key: `${this.kind}:${c.id}`,
      id: c.id,
      name: c.name,
      typeLabel: `EHR write connection — ${writeVendorLabel(c.sourceSystemType)}`,
      filterKeys: ['kind:ehr-write', `ehr-write:${c.sourceSystemType}`],
      audience: null,
      address: c.baseUrl || null,
      clientId: null,
      writeApis: writeApisLabel(c),
      isEnabled: c.isEnabled,
      actionBy: c.modifiedBy || c.createdBy || null,
      actionOn: c.modifiedOnUtc || c.createdOnUtc || null,
      raw: c,
    };
  }

  /** The vendors the role may create for (creatableWriteVendorCards), and only when it can also list the new row
   *  here (canList). */
  createCards(): ConnectionKindCard[] {
    if (!this.canList()) return [];
    return creatableWriteVendorCards(code => this.permissions.hasPermission(code)).map(toConnectionKindCard);
  }

  create(card: ConnectionKindCard): boolean {
    if (!this.actionGuard.ensure(
      writeConnectionCreateCodes(card.value),
      `You do not have permission to add a new ${card.label} write connection.`,
      'all',
    )) return false;
    this.editor().openNew(card.value as EhrVendor);
    return true;
  }

  /** Both rights, not only the vendor's: the server requires ehrwriteback.edit for any change to the write side. */
  editCodes(c: SourceConnectionModel): string[] {
    return ['ehrwriteback.edit', `${c.sourceSystemType.toLowerCase()}.edit`];
  }

  private isDeleteLocked(c: SourceConnectionModel): boolean {
    return this.usedWriteIds().has(c.id) || (c.access === 'ReadWrite' && this.usedReadIds().has(c.id));
  }

  /** No Test: there is no endpoint to test a saved EHR connection from a list. */
  rowActions(row: ConnectionRow): ConnectionRowAction[] {
    const c = row.raw as SourceConnectionModel;
    // Listing already needs the view rights, so View is always offered.
    const actions: ConnectionRowAction[] = [{ id: 'view', label: 'View', icon: 'visibility', disabled: false }];
    if (this.permissions.hasAll(this.editCodes(c))) {
      actions.push({ id: 'edit', label: 'Edit', icon: 'edit', disabled: false });
    }
    if (this.permissions.hasAll(sourceConnectionDeleteCodes(c))) {
      const locked = this.isDeleteLocked(c);
      actions.push({ id: 'delete', label: locked ? 'Delete (used by a workflow)' : 'Delete', icon: 'delete_outline', disabled: locked, danger: true });
    }
    return actions;
  }

  private readonly actionHandlers: Record<ConnectionActionId, (c: SourceConnectionModel) => void> = {
    view: c => this.editor().open(c, true),
    edit: c => this.openEdit(c),
    test: () => undefined,
    delete: c => this.confirmDelete(c),
  };

  runAction(id: ConnectionActionId, row: ConnectionRow): void {
    this.actionHandlers[id](row.raw as SourceConnectionModel);
  }

  /** No "used in a workflow" lock (unlike Source Connections): a write connection in use must stay editable, e.g. to
   *  tick "Vendor write APIs activated" once the practice turns them on. Safe for a Read & Write row a source
   *  workflow reads from because a write-purpose edit round-trips the saved retrieval and scopes. */
  openEdit(c: SourceConnectionModel): void {
    if (!this.actionGuard.ensure(this.editCodes(c), `You do not have permission to edit this ${writeVendorLabel(c.sourceSystemType)} write connection.`, 'all')) return;
    this.editor().open(c, false);
  }

  confirmDelete(c: SourceConnectionModel): void {
    if (this.isDeleteLocked(c)) return;
    if (!this.actionGuard.ensure(sourceConnectionDeleteCodes(c), `You do not have permission to delete this ${writeVendorLabel(c.sourceSystemType)} write connection.`, 'all')) return;
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete EHR write connection',
          message: `Delete the EHR write connection "${c.name}"?`
            + (c.access === 'ReadWrite' ? ' This connection is also used to read from the EHR. Deleting it removes it from Source Connections too.' : ''),
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(c.id).subscribe({
          next: () => {
            this.toast.success('EHR write connection deleted.');
            this.changed.emit();
          },
          error: (err: HttpErrorResponse) => {
            this.toast.error(err.error?.title ?? 'Deleting the EHR write connection failed. Try again.');
          },
        });
      });
  }
}
