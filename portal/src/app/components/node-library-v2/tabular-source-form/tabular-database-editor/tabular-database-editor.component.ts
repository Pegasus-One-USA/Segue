import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TabularSourceService, TabularSqlConnection } from '../../../../services/tabular-source.service';

/** Engines a CSV / SQL Table source can read. */
export const TABULAR_SQL_ENGINES = [
  { id: 'sqlserver', label: 'SQL Server / Azure SQL' },
  { id: 'postgresql', label: 'PostgreSQL' },
  { id: 'mysql', label: 'MySQL' },
] as const;

/**
 * Creates a saved database, or renames one / replaces its connection string. The connection string goes straight to
 * the API, which stores it as a secret; it is cleared from the form once saved and never returned. Used by the source
 * form's database picker and by the database kind on Source Connections.
 */
@Component({
  selector: 'app-tabular-database-editor',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './tabular-database-editor.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularDatabaseEditorComponent implements OnInit {
  private readonly api = inject(TabularSourceService);
  private readonly fb = inject(FormBuilder);

  /** Set to edit a saved database; unset to create one. */
  readonly existing = input<TabularSqlConnection | null>(null);
  readonly saved = output<TabularSqlConnection>();
  readonly cancelled = output<void>();

  readonly engines = TABULAR_SQL_ENGINES;
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    engine: ['sqlserver'],
    connectionString: [''],
  });

  ngOnInit(): void {
    const c = this.existing();
    if (c) this.form.patchValue({ name: c.name ?? '', engine: c.engine });
  }

  save(): void {
    const v = this.form.getRawValue();
    const editing = this.existing();
    if (!v.name.trim() || (!editing && !v.connectionString.trim())) {
      this.error.set(editing ? 'Enter a name.' : 'Name the database and enter its connection string.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    const request = editing?.id
      ? this.api.updateSqlConnection(editing.id, { name: v.name.trim(), connectionString: v.connectionString.trim() || null })
      : this.api.saveSqlConnection(v.engine, v.connectionString.trim(), v.name.trim());
    request.subscribe({
      next: result => {
        this.busy.set(false);
        // The connection string is now a secret; it never stays in the form.
        this.form.patchValue({ connectionString: '' });
        this.saved.emit(result);
      },
      error: (e: HttpErrorResponse) => {
        this.busy.set(false);
        const body = e.error as { message?: string; detail?: string; error_description?: string } | null;
        this.error.set(body?.message || body?.detail || body?.error_description || 'Saving the database failed. Try again.');
      },
    });
  }
}
