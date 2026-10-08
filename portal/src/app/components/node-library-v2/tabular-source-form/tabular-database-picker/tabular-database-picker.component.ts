import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TabularSourceService, TabularSqlConnection } from '../../../../services/tabular-source.service';
import { TabularDatabaseEditorComponent } from '../tabular-database-editor/tabular-database-editor.component';

/**
 * Step 1 of a database-backed CSV / SQL Table source: pick a saved database (the same list Source Connections
 * shows), test it, or create one here. Emits the chosen database, or null when none is chosen.
 */
@Component({
  selector: 'app-tabular-database-picker',
  standalone: true,
  imports: [TabularDatabaseEditorComponent],
  templateUrl: './tabular-database-picker.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularDatabasePickerComponent {
  private readonly api = inject(TabularSourceService);
  private readonly destroyRef = inject(DestroyRef);

  /** The database the node already reads, by id. */
  readonly selectedId = input<string | null>(null);
  readonly selectedChange = output<TabularSqlConnection | null>();

  readonly connections = signal<TabularSqlConnection[]>([]);
  readonly current = signal<string | null>(null);
  readonly adding = signal(false);
  readonly testing = signal(false);
  readonly test = signal<{ ok: boolean; message: string } | null>(null);
  readonly selected = computed(() => this.connections().find(c => c.id === this.current()) ?? null);

  constructor() {
    effect(() => {
      const id = this.selectedId();
      untracked(() => this.current.set(id));
    });
    this.load();
  }

  choose(id: string): void {
    this.current.set(id || null);
    this.test.set(null);
    this.selectedChange.emit(this.selected());
  }

  onSaved(saved: TabularSqlConnection): void {
    this.adding.set(false);
    this.load(saved.id ?? undefined);
  }

  runTest(): void {
    const id = this.current();
    if (!id) return;
    this.testing.set(true);
    this.api.testSqlConnection(id).subscribe({
      next: r => { this.testing.set(false); this.test.set(r); },
      error: () => { this.testing.set(false); this.test.set({ ok: false, message: 'The test could not run. Try again.' }); },
    });
  }

  private load(selectId?: string): void {
    this.api.listSqlConnections().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        this.connections.set(list);
        if (selectId) {
          this.choose(selectId);
        } else if (this.current()) {
          // The node's database is now known by name: tell the host, so it saves the current secret reference.
          this.selectedChange.emit(this.selected());
        }
      },
      error: () => this.connections.set([]),
    });
  }
}
