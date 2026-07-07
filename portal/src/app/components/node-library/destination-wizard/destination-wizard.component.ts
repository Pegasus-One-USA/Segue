import {
  Component, input, output, signal, computed, effect,
  untracked, inject, OnInit,
} from '@angular/core';
import {
  FormBuilder, Validators, ReactiveFormsModule,
} from '@angular/forms';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';

// ── Resource / field definitions (from HTML prototype) ─────────────────────────

export interface ResourceFieldDef {
  label: string;
  path:  string;
  sqlColumn: string;
  csvColumn: string;
}

export interface ResourceDef {
  scope:    string;
  sqlTable: string;
  csvFile:  string;
  fields:   ResourceFieldDef[];
}

export const DEST_RESOURCE_DEFS: Record<string, ResourceDef> = {
  Patient: {
    scope: 'user/Patient.read', sqlTable: 'dbo.Patient', csvFile: 'patients.csv',
    fields: [
      { label: 'Patient ID',    path: 'Patient.id',            sqlColumn: 'SourcePatientId', csvColumn: 'PatientId' },
      { label: 'First Name',    path: 'Patient.name.given',    sqlColumn: 'FirstName',       csvColumn: 'FirstName' },
      { label: 'Last Name',     path: 'Patient.name.family',   sqlColumn: 'LastName',        csvColumn: 'LastName' },
      { label: 'Date of Birth', path: 'Patient.birthDate',     sqlColumn: 'DateOfBirth',     csvColumn: 'DateOfBirth' },
      { label: 'Gender',        path: 'Patient.gender',        sqlColumn: 'Gender',          csvColumn: 'Gender' },
      { label: 'Phone',         path: 'Patient.telecom.value', sqlColumn: 'Phone',           csvColumn: 'Phone' },
    ],
  },
  Observation: {
    scope: 'user/Observation.read', sqlTable: 'dbo.PatientObservation', csvFile: 'observations.csv',
    fields: [
      { label: 'Observation ID', path: 'Observation.id',                   sqlColumn: 'SourceObservationId', csvColumn: 'ObservationId' },
      { label: 'Code',           path: 'Observation.code.coding.code',     sqlColumn: 'Code',               csvColumn: 'Code' },
      { label: 'Display',        path: 'Observation.code.coding.display',  sqlColumn: 'Display',            csvColumn: 'Display' },
      { label: 'Value',          path: 'Observation.valueQuantity.value',  sqlColumn: 'Value',              csvColumn: 'Value' },
      { label: 'Unit',           path: 'Observation.valueQuantity.unit',   sqlColumn: 'Unit',               csvColumn: 'Unit' },
      { label: 'Observed At',    path: 'Observation.effectiveDateTime',    sqlColumn: 'ObservedAt',         csvColumn: 'ObservedAt' },
    ],
  },
  Encounter: {
    scope: 'user/Encounter.read', sqlTable: 'dbo.Encounter', csvFile: 'encounters.csv',
    fields: [
      { label: 'Encounter ID',      path: 'Encounter.id',                sqlColumn: 'SourceEncounterId',  csvColumn: 'EncounterId' },
      { label: 'Status',            path: 'Encounter.status',            sqlColumn: 'Status',             csvColumn: 'Status' },
      { label: 'Class',             path: 'Encounter.class.code',        sqlColumn: 'ClassCode',          csvColumn: 'ClassCode' },
      { label: 'Patient Reference', path: 'Encounter.subject.reference', sqlColumn: 'PatientReference',   csvColumn: 'PatientReference' },
      { label: 'Start',             path: 'Encounter.period.start',      sqlColumn: 'StartDate',          csvColumn: 'StartDate' },
      { label: 'End',               path: 'Encounter.period.end',        sqlColumn: 'EndDate',            csvColumn: 'EndDate' },
    ],
  },
  Condition: {
    scope: 'user/Condition.read', sqlTable: 'dbo.Condition', csvFile: 'conditions.csv',
    fields: [
      { label: 'Condition ID',    path: 'Condition.id',                         sqlColumn: 'SourceConditionId', csvColumn: 'ConditionId' },
      { label: 'Code',            path: 'Condition.code.coding.code',           sqlColumn: 'Code',              csvColumn: 'Code' },
      { label: 'Display',         path: 'Condition.code.coding.display',        sqlColumn: 'Display',           csvColumn: 'Display' },
      { label: 'Clinical Status', path: 'Condition.clinicalStatus.coding.code', sqlColumn: 'ClinicalStatus',    csvColumn: 'ClinicalStatus' },
      { label: 'Onset Date',      path: 'Condition.onsetDateTime',              sqlColumn: 'OnsetDate',         csvColumn: 'OnsetDate' },
    ],
  },
  MedicationRequest: {
    scope: 'user/MedicationRequest.read', sqlTable: 'dbo.MedicationRequest', csvFile: 'medication_requests.csv',
    fields: [
      { label: 'Medication ID',   path: 'MedicationRequest.id',                                   sqlColumn: 'SourceMedicationRequestId', csvColumn: 'MedicationRequestId' },
      { label: 'Status',          path: 'MedicationRequest.status',                               sqlColumn: 'Status',                   csvColumn: 'Status' },
      { label: 'Intent',          path: 'MedicationRequest.intent',                               sqlColumn: 'Intent',                   csvColumn: 'Intent' },
      { label: 'Medication Code', path: 'MedicationRequest.medicationCodeableConcept.coding.code', sqlColumn: 'MedicationCode',           csvColumn: 'MedicationCode' },
      { label: 'Requester',       path: 'MedicationRequest.requester.reference',                  sqlColumn: 'RequesterReference',       csvColumn: 'RequesterReference' },
    ],
  },
};

export interface MappingRow {
  resource:   string;
  fieldLabel: string;
  fhirPath:   string;
  targetName: string;
  tableName:  string;
}

// ── Component ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-destination-wizard',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './destination-wizard.component.html',
  styleUrl: './destination-wizard.component.scss',
})
export class DestinationWizardComponent implements OnInit {
  private readonly fb = inject(FormBuilder);

  readonly destType   = input.required<'sql' | 'csv'>();
  readonly attachNode = input.required<CanvasNode>();
  readonly editNode   = input<CanvasNode | null>(null);

  readonly saved     = output<AddTransformEvent>();
  readonly cancelled = output<void>();

  // ── step state ────────────────────────────────────────────────────────────
  readonly step        = signal(1);
  readonly TOTAL_STEPS = 4;
  readonly STEP_LABELS = ['Configure', 'Data groups', 'Map fields', 'Review'];

  // ── forms ─────────────────────────────────────────────────────────────────
  readonly sqlForm = this.fb.group({
    name:      ['SQL Production', [Validators.required]],
    server:    ['', [Validators.required]],
    database:  ['', [Validators.required]],
    auth:      ['managed-identity', [Validators.required]],
    schema:    ['dbo', []],
    writeMode: ['upsert', []],
  });

  readonly csvForm = this.fb.group({
    name:        ['CSV Export', [Validators.required]],
    storageType: ['sftp', [Validators.required]],
    folder:      ['', [Validators.required]],
    filePattern: ['{resource}_{yyyyMMdd_HHmmss}.csv', [Validators.required]],
    delimiter:   ['comma', []],
    encoding:    ['utf-8', []],
  });

  // ── data groups ───────────────────────────────────────────────────────────
  readonly ALL_RESOURCES   = Object.keys(DEST_RESOURCE_DEFS);
  readonly selectedResources = signal<string[]>(['Patient', 'Observation', 'Encounter']);

  // ── mapping rows ──────────────────────────────────────────────────────────
  readonly mappingRows = signal<MappingRow[]>([]);

  // ── computed helpers ──────────────────────────────────────────────────────
  readonly isSql        = computed(() => this.destType() === 'sql');
  readonly destLabel    = computed(() => this.destType() === 'sql' ? 'SQL Server' : 'CSV');
  readonly resourceKeys = computed(() => this.selectedResources().filter(r => DEST_RESOURCE_DEFS[r]));

  readonly reviewSummary = computed(() => {
    const fv = this.isSql() ? this.sqlForm.value : this.csvForm.value;
    const rows = this.mappingRows();
    const resources = this.selectedResources();
    return { fv, rows, resources };
  });

  constructor() {
    // Rebuild mapping rows whenever selected resources or destType change
    effect(() => {
      const resources = this.selectedResources();
      const type      = this.destType();
      untracked(() => this._rebuildRows(resources, type));
    });
  }

  ngOnInit(): void {
    const edit = this.editNode();
    if (edit) this._populateFromNode(edit);
  }

  // ── step helpers ──────────────────────────────────────────────────────────
  isStepActive(n: number) { return this.step() === n; }
  isStepDone(n: number)   { return this.step() > n; }

  isNextDisabled(): boolean {
    const s = this.step();
    if (s === 1) return this.isSql() ? this.sqlForm.invalid : this.csvForm.invalid;
    if (s === 2) return this.selectedResources().length === 0;
    return false;
  }

  // ── navigation ────────────────────────────────────────────────────────────
  next():   void { if (this.step() < this.TOTAL_STEPS) this.step.update(x => x + 1); else this._save(); }
  back():   void { if (this.step() > 1) this.step.update(x => x - 1); }
  cancel(): void { this.cancelled.emit(); }

  // ── data groups ───────────────────────────────────────────────────────────
  isResourceSelected(r: string): boolean { return this.selectedResources().includes(r); }

  toggleResource(r: string): void {
    this.selectedResources.update(list =>
      list.includes(r) ? list.filter(x => x !== r) : [...list, r]
    );
  }

  // ── mapping rows ──────────────────────────────────────────────────────────
  rowsForResource(r: string): MappingRow[] {
    return this.mappingRows().filter(row => row.resource === r);
  }

  updateRow(i: number, field: 'targetName' | 'tableName', val: string): void {
    this.mappingRows.update(rows => {
      const next = [...rows];
      next[i] = { ...next[i], [field]: val };
      return next;
    });
  }

  // ── private ───────────────────────────────────────────────────────────────
  private _rebuildRows(resources: string[], type: 'sql' | 'csv'): void {
    const rows: MappingRow[] = [];
    for (const r of resources) {
      const def = DEST_RESOURCE_DEFS[r];
      if (!def) continue;
      def.fields.forEach(f => rows.push({
        resource:   r,
        fieldLabel: f.label,
        fhirPath:   f.path,
        targetName: type === 'sql' ? f.sqlColumn : f.csvColumn,
        tableName:  type === 'sql' ? def.sqlTable : def.csvFile,
      }));
    }
    this.mappingRows.set(rows);
  }

  private _populateFromNode(node: CanvasNode): void {
    const f = node.fields ?? {};
    if (this.destType() === 'sql') {
      this.sqlForm.patchValue({
        name:      f['dest_name']      || 'SQL Production',
        server:    f['dest_server']    || '',
        database:  f['dest_database']  || '',
        auth:      f['dest_auth']      || 'managed-identity',
        schema:    f['dest_schema']    || 'dbo',
        writeMode: f['dest_writeMode'] || 'upsert',
      });
    } else {
      this.csvForm.patchValue({
        name:        f['dest_name']        || 'CSV Export',
        storageType: f['dest_storageType'] || 'sftp',
        folder:      f['dest_folder']      || '',
        filePattern: f['dest_filePattern'] || '{resource}_{yyyyMMdd_HHmmss}.csv',
        delimiter:   f['dest_delimiter']   || 'comma',
        encoding:    f['dest_encoding']    || 'utf-8',
      });
    }
    if (f['dest_resources']) {
      this.selectedResources.set(f['dest_resources'].split(',').filter(Boolean));
    }
  }

  private _save(): void {
    const type = this.destType();
    const config: Record<string, string> = {
      dest_resources: this.selectedResources().join(','),
    };

    if (type === 'sql') {
      const v = this.sqlForm.value;
      config['dest_name']      = v.name      ?? '';
      config['dest_server']    = v.server    ?? '';
      config['dest_database']  = v.database  ?? '';
      config['dest_auth']      = v.auth      ?? '';
      config['dest_schema']    = v.schema    ?? 'dbo';
      config['dest_writeMode'] = v.writeMode ?? 'upsert';
    } else {
      const v = this.csvForm.value;
      config['dest_name']        = v.name        ?? '';
      config['dest_storageType'] = v.storageType ?? '';
      config['dest_folder']      = v.folder      ?? '';
      config['dest_filePattern'] = v.filePattern ?? '';
      config['dest_delimiter']   = v.delimiter   ?? 'comma';
      config['dest_encoding']    = v.encoding    ?? 'utf-8';
    }

    // Store mapping row overrides as JSON
    config['dest_mappingCount'] = String(this.mappingRows().length);

    this.saved.emit({
      attachNode:  this.attachNode(),
      transformId: type === 'sql' ? 'dest-sqlserver' : 'dest-csv',
      status:      'enabled',
      config,
    });
  }
}
