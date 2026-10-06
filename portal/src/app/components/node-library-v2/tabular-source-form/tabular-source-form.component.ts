import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { SourceConfigFormComponent } from '../../shared-v2/config-form/config-form-v2.contract';
import {
  TabularPreview,
  TabularSourceService,
  TabularTemplatePreset,
} from '../../../services/tabular-source.service';

const CONNECTOR = 'CSV / SQL Table';
const DEFAULT_TYPES = ['Patient', 'AllergyIntolerance'];

/**
 * Headless form for the CSV / SQL Table source (TabularSourceNode): rows from an uploaded CSV or a SQL query,
 * turned into FHIR resources by templates. Same contract as Hl7v2SourceFormComponent (getFields() returns a flat
 * field bag). The CSV itself and the connection string go straight to the API; the node keeps only the file id and
 * the secret reference, never the data or the password.
 */
@Component({
  selector: 'app-tabular-source-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './tabular-source-form.component.html',
  styleUrl: './tabular-source-form.component.scss',
})
export class TabularSourceFormComponent implements SourceConfigFormComponent {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(TabularSourceService);
  private readonly destroyRef = inject(DestroyRef);

  readonly initialFields = input<Record<string, string> | null>(null);

  readonly engines = [
    { id: 'sqlserver', label: 'SQL Server / Azure SQL' },
    { id: 'postgresql', label: 'PostgreSQL' },
    { id: 'mysql', label: 'MySQL' },
  ];

  readonly form = this.fb.nonNullable.group({
    name: [CONNECTOR, [Validators.required]],
    datasetKey: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9][A-Za-z0-9 _.-]{1,62}[A-Za-z0-9]$/)]],
    kind: ['csv' as 'csv' | 'sql'],
    sqlEngine: ['sqlserver'],
    connectionString: [''],
    query: [''],
    maxRows: ['5000', [Validators.pattern(/^\d{1,5}$/)]],
    templates: [''],
  });

  readonly presets = signal<TabularTemplatePreset[]>([]);
  readonly selectedTypes = signal<string[]>(DEFAULT_TYPES);
  readonly fileId = signal<string | null>(null);
  readonly fileName = signal<string | null>(null);
  readonly fileRows = signal<number | null>(null);
  readonly fileColumns = signal<string[]>([]);
  readonly secretKeyVaultName = signal<string | null>(null);
  readonly secretName = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly preview = signal<TabularPreview | null>(null);
  readonly kind = signal<'csv' | 'sql'>('csv');

  /** A method, not a computed: it reads the query control, which is not a signal. */
  hasSource(): boolean {
    return this.kind() === 'csv' ? !!this.fileId() : !!this.secretName() && !!this.form.controls.query.value.trim();
  }

  constructor() {
    this.api.templatePresets().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: presets => {
        this.presets.set(presets);
        if (!this.form.controls.templates.value) this.applyPresets();
      },
    });

    this.form.controls.kind.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(k => this.kind.set(k));

    effect(() => {
      const fields = this.initialFields();
      untracked(() => {
        if (fields) this.populate(fields);
      });
    });
  }

  togglePreset(type: string, checked: boolean): void {
    const current = new Set(this.selectedTypes());
    if (checked) current.add(type); else current.delete(type);
    this.selectedTypes.set(this.presets().map(p => p.resourceType).filter(t => current.has(t)));
    this.applyPresets();
  }

  /** Rebuilds the templates from the ticked presets. Hand edits are replaced, so the hint says so. */
  applyPresets(): void {
    const chosen = this.presets().filter(p => this.selectedTypes().includes(p.resourceType));
    const stored = chosen.map(p => ({ resourceType: p.resourceType, template: JSON.parse(p.template) }));
    this.form.controls.templates.setValue(JSON.stringify(stored, null, 2));
  }

  onFileChosen(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    this.busy.set('upload');
    this.error.set(null);
    this.api.upload(file).subscribe({
      next: uploaded => {
        this.busy.set(null);
        this.fileId.set(uploaded.id);
        this.fileName.set(uploaded.fileName);
        this.fileRows.set(uploaded.rowCount);
        this.fileColumns.set(uploaded.columns);
        this.preview.set(null);
      },
      error: (e: HttpErrorResponse) => { this.busy.set(null); this.error.set(this.messageOf(e)); },
    });
  }

  saveConnection(): void {
    const connectionString = this.form.controls.connectionString.value.trim();
    if (!connectionString) {
      this.error.set('Enter the connection string first.');
      return;
    }

    this.busy.set('connection');
    this.error.set(null);
    this.api.saveSqlConnection(this.form.controls.sqlEngine.value, connectionString).subscribe({
      next: saved => {
        this.busy.set(null);
        this.secretKeyVaultName.set(saved.secretKeyVaultName);
        this.secretName.set(saved.secretName);
        // The connection string is now a secret; it never stays in the form or on the node.
        this.form.controls.connectionString.setValue('');
      },
      error: (e: HttpErrorResponse) => { this.busy.set(null); this.error.set(this.messageOf(e)); },
    });
  }

  runPreview(): void {
    this.busy.set('preview');
    this.error.set(null);
    const v = this.form.getRawValue();
    this.api.preview({
      kind: v.kind,
      fileId: this.fileId(),
      sqlEngine: v.sqlEngine,
      secretKeyVaultName: this.secretKeyVaultName(),
      secretName: this.secretName(),
      query: v.query,
      templates: v.templates,
    }).subscribe({
      next: result => { this.busy.set(null); this.preview.set(result); },
      error: (e: HttpErrorResponse) => { this.busy.set(null); this.error.set(this.messageOf(e)); },
    });
  }

  prettyJson(json: string): string {
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }

  /** null ⇒ fix the highlighted fields — the SourceConfigFormComponent contract. */
  getFields(): Record<string, string> | null {
    this.form.markAllAsTouched();
    if (this.form.invalid || !this.hasSource() || !this.templatesAreJson()) {
      if (!this.hasSource()) {
        this.error.set(this.kind() === 'csv'
          ? 'Upload the CSV file.'
          : 'Save the database connection and enter the query.');
      } else if (!this.templatesAreJson()) {
        this.error.set('The templates are not valid JSON.');
      }
      return null;
    }

    const v = this.form.getRawValue();
    const sql = v.kind === 'sql';
    return {
      __name: v.name || CONNECTOR,
      Connector: CONNECTOR,
      'App context': 'Tabular data',
      tab_kind: v.kind,
      tab_datasetKey: v.datasetKey.trim(),
      tab_fileId: sql ? '' : this.fileId() ?? '',
      tab_fileName: sql ? '' : this.fileName() ?? '',
      tab_sqlEngine: sql ? v.sqlEngine : '',
      tab_secretKeyVaultName: sql ? this.secretKeyVaultName() ?? '' : '',
      tab_secretName: sql ? this.secretName() ?? '' : '',
      tab_query: sql ? v.query.trim() : '',
      tab_maxRows: v.maxRows || '5000',
      tab_templates: JSON.stringify(JSON.parse(v.templates)),
      // Read by the node library to offer downstream destinations these resource types.
      Resources: this.templateTypes().join(', '),
    };
  }

  private templatesAreJson(): boolean {
    try {
      const parsed = JSON.parse(this.form.controls.templates.value);
      return Array.isArray(parsed) && parsed.length > 0;
    } catch {
      return false;
    }
  }

  private templateTypes(): string[] {
    try {
      return (JSON.parse(this.form.controls.templates.value) as { resourceType?: string }[])
        .map(t => t.resourceType ?? '').filter(Boolean);
    } catch {
      return [];
    }
  }

  private populate(fields: Record<string, string>): void {
    const kind = fields['tab_kind'] === 'sql' ? 'sql' : 'csv';
    this.form.patchValue({
      name: fields['__name'] || CONNECTOR,
      datasetKey: fields['tab_datasetKey'] || '',
      kind,
      sqlEngine: fields['tab_sqlEngine'] || 'sqlserver',
      query: fields['tab_query'] || '',
      maxRows: fields['tab_maxRows'] || '5000',
    });
    this.kind.set(kind);
    if (fields['tab_templates']) {
      try {
        this.form.controls.templates.setValue(JSON.stringify(JSON.parse(fields['tab_templates']), null, 2));
        this.selectedTypes.set(this.templateTypes());
      } catch {
        this.form.controls.templates.setValue(fields['tab_templates']);
      }
    }

    this.fileId.set(fields['tab_fileId'] || null);
    this.fileName.set(fields['tab_fileName'] || null);
    this.secretKeyVaultName.set(fields['tab_secretKeyVaultName'] || null);
    this.secretName.set(fields['tab_secretName'] || null);
    if (this.fileId()) {
      this.api.getFile(this.fileId()!).subscribe({
        next: f => { this.fileRows.set(f.rowCount); this.fileColumns.set(f.columns); },
        error: () => this.error.set('The uploaded CSV for this source no longer exists. Upload it again.'),
      });
    }
  }

  private messageOf(e: HttpErrorResponse): string {
    const body = e.error as { message?: string; error_description?: string; error?: string } | null;
    return body?.message || body?.error_description || body?.error || 'The request failed. Try again.';
  }
}
