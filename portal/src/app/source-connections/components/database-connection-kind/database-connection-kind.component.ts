import { Component, computed, forwardRef, inject, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { TabularDatabaseEditorComponent } from '../../../components/node-library-v2/tabular-source-form/tabular-database-editor/tabular-database-editor.component';
import { TabularSourceService, TabularSqlConnection } from '../../../services/tabular-source.service';
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
import { databaseTypeLabel } from '../../../connections/connection-labels';

/**
 * The databases CSV / SQL Table sources read, on the Source Connections page: listed by name and engine (never the
 * connection string), with Test (connect, check the login can only read, run SELECT 1), Edit (rename or replace the
 * connection string; every workflow using it follows), Delete and New. Gated by the CSV / SQL Table permissions
 * (tabularsources.view / .edit / .delete).
 */
@Component({
  selector: 'app-database-connection-kind',
  standalone: true,
  imports: [TabularDatabaseEditorComponent],
  providers: [{ provide: CONNECTION_KIND_HOST, useExisting: forwardRef(() => DatabaseConnectionKindComponent) }],
  templateUrl: './database-connection-kind.component.html',
  styleUrl: './database-connection-kind.component.scss',
})
export class DatabaseConnectionKindComponent implements ConnectionKindHost {
  private readonly api = inject(TabularSourceService);
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly kind = 'database' as const;
  readonly loadErrorMessage = 'Loading database connections failed. Try again.';

  readonly changed = output<void>();

  /** The database being edited, 'new', or null when the editor is closed. */
  readonly editing = signal<TabularSqlConnection | 'new' | null>(null);
  readonly editingExisting = computed(() => {
    const editing = this.editing();
    return editing === 'new' ? null : editing;
  });

  canList(): boolean {
    return this.permissions.hasPermission('tabularsources.view');
  }

  list(): Observable<ConnectionRow[]> {
    return this.api.listSqlConnections().pipe(
      map(list => (Array.isArray(list) ? list : [])
        .filter((c): c is TabularSqlConnection & { id: string } => !!c.id)
        .map(c => this.toRow(c))),
    );
  }

  private toRow(c: TabularSqlConnection & { id: string }): ConnectionRow {
    return {
      kind: this.kind,
      key: `${this.kind}:${c.id}`,
      id: c.id,
      name: c.name || 'Unnamed database',
      typeLabel: databaseTypeLabel(c.engine),
      filterKeys: ['kind:database', `engine:${c.engine}`],
      audience: null,
      address: null,
      clientId: null,
      writeApis: null,
      isEnabled: null,
      actionBy: c.createdBy,
      actionOn: c.updatedOnUtc,
      raw: c,
    };
  }

  createCards(): ConnectionKindCard[] {
    if (!this.canList() || !this.permissions.hasPermission('tabularsources.edit')) return [];
    return [{
      kind: this.kind,
      value: 'Database',
      label: 'SQL database',
      sub: 'SQL Server, Azure SQL, PostgreSQL or MySQL. One query per resource type; the connection string is kept as a secret.',
      abbr: 'SQL',
      color: 'var(--color-primary)',
    }];
  }

  create(): boolean {
    if (!this.actionGuard.ensure('tabularsources.edit', 'You do not have permission to add a database connection.')) return false;
    this.editing.set('new');
    return true;
  }

  rowActions(): ConnectionRowAction[] {
    const actions: ConnectionRowAction[] = [];
    if (this.permissions.hasPermission('tabularsources.edit')) {
      actions.push({ id: 'test', label: 'Test', icon: 'network_check', disabled: false });
      actions.push({ id: 'edit', label: 'Edit', icon: 'edit', disabled: false });
    }
    if (this.permissions.hasPermission('tabularsources.delete')) {
      actions.push({ id: 'delete', label: 'Delete', icon: 'delete_outline', disabled: false, danger: true });
    }
    return actions;
  }

  private readonly actionHandlers: Record<ConnectionActionId, (c: TabularSqlConnection & { id: string }, name: string) => void> = {
    view: () => undefined,
    test: c => this.test(c.id),
    edit: c => this.editing.set(c),
    delete: (c, name) => this.confirmDelete(c.id, name),
  };

  runAction(id: ConnectionActionId, row: ConnectionRow): void {
    this.actionHandlers[id](row.raw as TabularSqlConnection & { id: string }, row.name);
  }

  private test(id: string): void {
    this.api.testSqlConnection(id).subscribe({
      next: result => (result.ok ? this.toast.success(result.message) : this.toast.error(result.message)),
      error: () => this.toast.error('The test could not run. Try again.'),
    });
  }

  private confirmDelete(id: string, name: string): void {
    if (!this.actionGuard.ensure('tabularsources.delete', 'You do not have permission to delete this database connection.')) return;
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete database connection',
          message: `Delete the database connection "${name}"? Workflows that read it keep their own copy of the reference.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.api.deleteSqlConnection(id).subscribe({
          next: () => {
            this.toast.success('Database connection deleted.');
            this.changed.emit();
          },
          error: () => this.toast.error('Deleting the database connection failed. Try again.'),
        });
      });
  }

  onSaved(): void {
    this.editing.set(null);
    this.toast.success('Database connection saved.');
    this.changed.emit();
  }

  onBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.editing.set(null);
  }
}
