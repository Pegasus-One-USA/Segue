import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { TabularDatabaseEditorComponent } from '../../../components/node-library-v2/tabular-source-form/tabular-database-editor/tabular-database-editor.component';
import { TabularSourceService, TabularSqlConnection } from '../../../services/tabular-source.service';

/**
 * The databases CSV / SQL Table sources read, on the Source Connections page: listed by name and engine (never the
 * connection string), with Test (connect, check the login can only read, run SELECT 1), Edit (rename or replace the
 * connection string; every workflow using it follows), Delete and New. Gated by the CSV / SQL Table permissions.
 */
@Component({
  selector: 'app-database-connections-panel',
  standalone: true,
  imports: [DatePipe, HideWithoutPermissionDirective, TabularDatabaseEditorComponent],
  templateUrl: './database-connections-panel.component.html',
  styleUrl: './database-connections-panel.component.scss',
})
export class DatabaseConnectionsPanelComponent {
  private readonly api = inject(TabularSourceService);
  private readonly destroyRef = inject(DestroyRef);

  readonly connections = signal<TabularSqlConnection[]>([]);
  readonly loading = signal(true);
  /** 'new', a connection id being edited, or null. */
  readonly editing = signal<string | null>(null);
  readonly results = signal<Record<string, { ok: boolean; message: string }>>({});
  readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.listSqlConnections().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => { this.loading.set(false); this.connections.set(list); },
      error: () => { this.loading.set(false); this.connections.set([]); },
    });
  }

  editingConnection(): TabularSqlConnection | null {
    return this.connections().find(c => c.id === this.editing()) ?? null;
  }

  onSaved(): void {
    this.editing.set(null);
    this.load();
  }

  test(c: TabularSqlConnection): void {
    if (!c.id) return;
    const id = c.id;
    this.results.update(r => ({ ...r, [id]: { ok: true, message: 'Testing…' } }));
    this.api.testSqlConnection(id).subscribe({
      next: result => this.results.update(r => ({ ...r, [id]: result })),
      error: () => this.results.update(r => ({ ...r, [id]: { ok: false, message: 'The test could not run. Try again.' } })),
    });
  }

  remove(c: TabularSqlConnection): void {
    if (!c.id || !window.confirm(`Delete the database connection "${c.name}"? Workflows that read it keep their own copy of the reference.`)) {
      return;
    }

    this.error.set(null);
    this.api.deleteSqlConnection(c.id).subscribe({
      next: () => this.load(),
      error: () => this.error.set('Deleting the database connection failed. Try again.'),
    });
  }
}
