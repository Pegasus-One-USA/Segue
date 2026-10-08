import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { SourceConfigFormComponent } from '../../shared-v2/config-form/config-form-v2.contract';
import {
  TabularCheckResult,
  TabularSourceFile,
  TabularSourceService,
  TabularSqlConnection,
  TabularStreamCheck,
  TabularTemplatePreset,
} from '../../../services/tabular-source.service';
import { TabularCsvFilesComponent } from './tabular-csv-files/tabular-csv-files.component';
import { TabularDatabasePickerComponent } from './tabular-database-picker/tabular-database-picker.component';
import { TabularEntry } from './tabular-entry.model';
import { TabularStreamCardComponent } from './tabular-stream-card/tabular-stream-card.component';

const CONNECTOR = 'CSV / SQL Table';

/** Resource types offered in "Resource types to read". Those with a starting template come first. */
const READABLE_TYPES = [
  'Patient', 'AllergyIntolerance', 'Condition', 'Observation', 'DocumentReference', 'MedicationRequest',
  'MedicationStatement', 'Immunization', 'Procedure', 'Encounter', 'DiagnosticReport', 'QuestionnaireResponse',
  'ServiceRequest', 'CarePlan', 'Goal', 'Coverage', 'BodyStructure', 'Communication', 'Practitioner', 'Organization',
  'Location',
];

interface SecretRef {
  engine: string;
  vault: string;
  name: string;
}

/**
 * Headless form for the CSV / SQL Table source (TabularSourceNode). It puts the steps together; each step is its own
 * component: where rows come from (TabularDatabasePickerComponent / TabularCsvFilesComponent), the resource types to
 * read, and one TabularStreamCardComponent per type (its own query or file, and its own template). "Check" asks the
 * API to verify every query or file against what it reads without reading a row; the form will not save until the
 * current settings have passed. Same contract as the other source forms (getFields() returns a flat field bag).
 */
@Component({
  selector: 'app-tabular-source-form',
  standalone: true,
  imports: [ReactiveFormsModule, TabularDatabasePickerComponent, TabularCsvFilesComponent, TabularStreamCardComponent],
  templateUrl: './tabular-source-form.component.html',
  styleUrl: './tabular-form-shared.scss',
})
export class TabularSourceFormComponent implements SourceConfigFormComponent {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(TabularSourceService);
  private readonly destroyRef = inject(DestroyRef);

  readonly initialFields = input<Record<string, string> | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: [CONNECTOR, [Validators.required]],
    datasetKey: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9][A-Za-z0-9 _.-]{1,62}[A-Za-z0-9]$/)]],
    kind: ['sql' as 'csv' | 'sql'],
    maxRows: ['5000', [Validators.pattern(/^\d{1,5}$/)]],
  });

  readonly kind = signal<'csv' | 'sql'>('sql');
  readonly presets = signal<TabularTemplatePreset[]>([]);
  readonly connectionId = signal<string | null>(null);
  private readonly connection = signal<TabularSqlConnection | null>(null);
  /** Kept from a node whose database is not a saved one (saved before databases were listed), so it still runs. */
  private readonly legacySecret = signal<SecretRef | null>(null);
  readonly files = signal<TabularSourceFile[]>([]);
  readonly entries = signal<TabularEntry[]>([]);
  readonly busy = signal<'check' | 'preview' | null>(null);
  readonly error = signal<string | null>(null);
  readonly result = signal<TabularCheckResult | null>(null);
  /** The settings the last check ran on: saving needs a passed check of exactly the current settings. */
  private readonly checkedSignature = signal<string | null>(null);

  readonly types = computed(() => {
    const withPreset = this.presets().map(p => p.resourceType);
    return [...withPreset, ...READABLE_TYPES.filter(t => !withPreset.includes(t))];
  });
  readonly selectedTypes = computed(() => this.entries().map(e => e.resourceType));

  constructor() {
    this.api.templatePresets().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: p => this.presets.set(p) });
    this.form.controls.kind.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(k => this.kind.set(k));

    effect(() => {
      const fields = this.initialFields();
      untracked(() => {
        if (fields) this.populate(fields);
      });
    });
  }

  onConnection(connection: TabularSqlConnection | null): void {
    this.connection.set(connection);
    this.connectionId.set(connection?.id ?? null);
    if (connection) this.legacySecret.set(null);
  }

  onFileUploaded(file: TabularSourceFile): void {
    this.files.update(list => [...list.filter(f => f.id !== file.id), file]);
    // A type with no file yet reads the one just uploaded.
    this.entries.update(list => list.map(e => (e.fileId ? e : { ...e, fileId: file.id })));
  }

  toggleType(type: string, checked: boolean): void {
    if (checked && !this.selectedTypes().includes(type)) {
      this.entries.update(list => [...list, this.newEntry(type)]);
    } else if (!checked) {
      this.entries.update(list => list.filter(e => e.resourceType !== type));
    }
  }

  updateEntry(index: number, change: Partial<TabularEntry>): void {
    this.entries.update(list => list.map((e, i) => (i === index ? { ...e, ...change } : e)));
  }

  resetTemplate(index: number): void {
    const type = this.entries()[index]?.resourceType;
    if (type) this.updateEntry(index, { template: this.startingTemplate(type) });
  }

  /** The last check of exactly the current settings for this entry, or null. */
  checkFor(index: number): TabularStreamCheck | null {
    return this.isChecked() ? this.result()?.streams.find(s => s.index === index) ?? null : null;
  }

  isChecked(): boolean {
    return this.checkedSignature() !== null && this.checkedSignature() === this.signature();
  }

  check(preview = false): void {
    this.error.set(null);
    const streams = this.streamsJson();
    if (streams === null) return;
    const secret = this.secret();
    if (this.kind() === 'sql' && !secret) {
      this.error.set('Choose the database these queries read.');
      return;
    }

    const signature = this.signature();
    this.busy.set(preview ? 'preview' : 'check');
    this.api.check({
      kind: this.kind(),
      sqlEngine: secret?.engine ?? null,
      secretKeyVaultName: secret?.vault ?? null,
      secretName: secret?.name ?? null,
      streams,
      preview,
    }).subscribe({
      next: result => {
        this.busy.set(null);
        this.result.set(result);
        this.checkedSignature.set(signature);
      },
      error: (e: HttpErrorResponse) => {
        this.busy.set(null);
        const body = e.error as { message?: string; detail?: string; error_description?: string } | null;
        this.error.set(body?.message || body?.detail || body?.error_description || 'The check failed. Try again.');
      },
    });
  }

  /** null ⇒ fix the highlighted fields — the SourceConfigFormComponent contract. */
  getFields(): Record<string, string> | null {
    this.form.markAllAsTouched();
    const streams = this.streamsJson();
    if (this.form.controls.name.invalid || this.form.controls.datasetKey.invalid || this.form.controls.maxRows.invalid || streams === null) {
      return null;
    }

    const secret = this.secret();
    if (this.kind() === 'sql' && !secret) {
      this.error.set('Choose the database these queries read.');
      return null;
    }

    if (!this.isChecked() || !this.result()?.allPassed) {
      this.error.set('Run Check and fix what it reports before saving. Every resource type must pass.');
      return null;
    }

    const v = this.form.getRawValue();
    const sql = this.kind() === 'sql';
    return {
      __name: v.name || CONNECTOR,
      Connector: CONNECTOR,
      'App context': 'Tabular data',
      tab_kind: this.kind(),
      tab_datasetKey: v.datasetKey.trim(),
      tab_sqlConnectionId: sql ? this.connectionId() ?? '' : '',
      tab_sqlConnectionName: sql ? this.connection()?.name ?? '' : '',
      tab_sqlEngine: sql ? secret!.engine : '',
      tab_secretKeyVaultName: sql ? secret!.vault : '',
      tab_secretName: sql ? secret!.name : '',
      tab_maxRows: v.maxRows || '5000',
      tab_streams: streams,
      // The older single-query settings are cleared: this node now reads each type on its own.
      tab_templates: '',
      tab_query: '',
      tab_fileId: '',
      tab_fileName: '',
      // Read by the node library to offer downstream destinations these resource types.
      Resources: this.selectedTypes().join(', '),
    };
  }

  /** The entries as the node stores them, or null (with the reason shown) when one is incomplete. */
  private streamsJson(): string | null {
    const entries = this.entries();
    if (entries.length === 0) {
      this.error.set('Choose at least one resource type to read.');
      return null;
    }

    const sql = this.kind() === 'sql';
    const stored = [];
    for (const e of entries) {
      let template: unknown;
      try {
        template = JSON.parse(e.template);
      } catch {
        this.error.set(`${e.resourceType}: the template is not valid JSON.`);
        return null;
      }

      if (sql && !e.query.trim()) {
        this.error.set(`${e.resourceType}: enter the query that returns its rows.`);
        return null;
      }

      if (!sql && !e.fileId) {
        this.error.set(`${e.resourceType}: choose the CSV file its rows come from.`);
        return null;
      }

      const filtered = !sql && !!e.rowFilterColumn && !!e.rowFilterValue.trim();
      stored.push({
        resourceType: e.resourceType,
        query: sql ? e.query.trim() : null,
        fileId: sql ? null : e.fileId,
        rowFilterColumn: filtered ? e.rowFilterColumn : null,
        rowFilterValue: filtered ? e.rowFilterValue.trim() : null,
        template,
      });
    }

    return JSON.stringify(stored);
  }

  private secret(): SecretRef | null {
    const c = this.connection();
    return c ? { engine: c.engine, vault: c.secretKeyVaultName, name: c.secretName } : this.legacySecret();
  }

  private signature(): string {
    return JSON.stringify([this.kind(), this.secret()?.name ?? null, this.entries()]);
  }

  private newEntry(type: string): TabularEntry {
    return {
      resourceType: type,
      query: '',
      fileId: this.files()[0]?.id ?? '',
      rowFilterColumn: '',
      rowFilterValue: '',
      template: this.startingTemplate(type),
    };
  }

  private startingTemplate(type: string): string {
    const preset = this.presets().find(p => p.resourceType === type);
    const template = preset
      ? JSON.parse(preset.template)
      : { resourceType: type, id: '{{id}}', ...(type === 'Patient' ? {} : { subject: { reference: 'Patient/{{patient_id}}' } }) };
    return JSON.stringify(template, null, 2);
  }

  private populate(fields: Record<string, string>): void {
    const kind = fields['tab_kind'] === 'csv' ? 'csv' : 'sql';
    this.form.patchValue({
      name: fields['__name'] || CONNECTOR,
      datasetKey: fields['tab_datasetKey'] || '',
      kind,
      maxRows: fields['tab_maxRows'] || '5000',
    });
    this.kind.set(kind);
    this.connectionId.set(fields['tab_sqlConnectionId'] || null);
    if (!fields['tab_sqlConnectionId'] && fields['tab_secretName']) {
      this.legacySecret.set({ engine: fields['tab_sqlEngine'] || 'sqlserver', vault: fields['tab_secretKeyVaultName'] || '', name: fields['tab_secretName'] });
    }

    let entries: TabularEntry[] = [];
    try {
      if (fields['tab_streams']) {
        entries = (JSON.parse(fields['tab_streams']) as {
          resourceType: string; query?: string; fileId?: string; rowFilterColumn?: string; rowFilterValue?: string; template: unknown;
        }[]).map(s => ({
          resourceType: s.resourceType,
          query: s.query ?? '',
          fileId: s.fileId ?? '',
          rowFilterColumn: s.rowFilterColumn ?? '',
          rowFilterValue: s.rowFilterValue ?? '',
          template: JSON.stringify(s.template, null, 2),
        }));
      } else if (fields['tab_templates']) {
        // An older node: one query or file through every template. Each template becomes its own type reading that
        // same query or file, which builds exactly what the node built before.
        entries = (JSON.parse(fields['tab_templates']) as { resourceType: string; template: unknown }[]).map(t => ({
          resourceType: t.resourceType,
          query: fields['tab_query'] ?? '',
          fileId: fields['tab_fileId'] ?? '',
          rowFilterColumn: '',
          rowFilterValue: '',
          template: JSON.stringify(t.template, null, 2),
        }));
      }
    } catch {
      this.error.set('The saved resource types could not be read. Choose them again.');
    }

    this.entries.set(entries);
    for (const fileId of new Set(entries.map(e => e.fileId).filter(Boolean))) {
      this.api.getFile(fileId).subscribe({
        next: f => this.files.update(list => [...list.filter(x => x.id !== f.id), f]),
        error: () => this.error.set('An uploaded CSV for this source no longer exists. Upload it again.'),
      });
    }
  }
}
