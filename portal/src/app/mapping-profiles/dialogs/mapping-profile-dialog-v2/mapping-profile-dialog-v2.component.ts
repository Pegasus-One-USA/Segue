import { Component, inject, signal, computed, effect, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { MappingRow } from '../../../components/node-library/destination-wizard/mapping-profile-form.component';
import { checkColumnTypeCompatibility, isSqlFamilyDestType } from '../../../components/node-library/destination-wizard/field-mapping/field-mapping-model';
import { MappingProfileCanvasComponent } from '../mapping-profile-dialog/mapping-profile-canvas/mapping-profile-canvas.component';
import { MappingProfileService } from '../../services/mapping-profile.service';
import { DestinationSchemaService, DestinationTable } from '../../../services/destination-schema.service';
import {
  ArrayPolicyType,
  CreateMappingProfileRequest,
  MappingFieldDto,
  MappingProfileDto,
  MappingValueType,
} from '../../models/mapping-profile.model';
import { SOURCE_CONNECTIONS_ENDPOINTS, DESTINATION_ENDPOINTS } from '../../../core/api-endpoints';
import { DestinationType } from '../../../destination-connections/models/destination-configuration.model';
import { SUPPORTED_RESOURCE_TYPES } from '../../../data/scope-constants.data';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

export interface MappingProfileDialogV2Data {
  mode: 'create' | 'edit' | 'view';
  mappingProfile?: MappingProfileDto;
}

interface NamedEntity {
  id: string;
  name: string;
  destinationType?: DestinationType;
}

/** Which of MappingProfileFormComponent's destType branches a saved destination's real (22-value) DestinationType
 *  should render as — mirrors destination-connection-dialog.component.ts's toFormType/toEngine, widened to cover
 *  Mongo too. Every type this screen has no dedicated branch for (Snowflake, S3, Ndjson, ...) falls back to
 *  'csv' — free-text target-field entry, no live column verification, which is what those types get in the
 *  destination wizard today as well (§2 of the plan: "Mapping-owned ... free text").
 */
function toDestKind(t: DestinationType | undefined): 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres' {
  if (t === 'SqlServer' || t === 'AzureSql') return 'sql';
  if (t === 'MySql') return 'mysql';
  if (t === 'PostgreSql') return 'postgres';
  if (t === 'Mongo') return 'mongo';
  return 'csv';
}

/** MappingFieldDto -> MappingRow (docs/backend/14-mapping-profile-master-screen-plan.md §3.2 translation table). */
function toMappingRow(resourceType: string, destinationObject: string, f: MappingFieldDto): MappingRow {
  const rawPath = f.jsonPath.replace(/^\$\.?/, '').replace(/\[\*\]/g, '');
  const fhirPath = rawPath ? `${resourceType}.${rawPath}` : `${resourceType}.${f.targetField}`;
  const fieldLabel = rawPath
    ? rawPath.split('.').map(s => s.charAt(0).toUpperCase() + s.slice(1)).join(' ')
    : f.targetField;

  return {
    resource: resourceType,
    fieldLabel,
    fhirPath,
    targetName: f.targetField,
    tableName: destinationObject,
    jsonPath: f.jsonPath,
    valueType: f.valueType,
    arrays: f.arrayAncestors ?? undefined,
    isUpsertKey: f.isUpsertKey ?? false,
    isRequired: f.isRequired,
    defaultValue: f.defaultValue ?? undefined,
    format: f.format ?? undefined,
    normalizationType: f.normalizationType ?? undefined,
    terminologySystemJsonPath: f.terminologySystemJsonPath ?? undefined,
    terminologyCodeJsonPath: f.terminologyCodeJsonPath ?? undefined,
    arrayPolicy: f.arrayPolicy,
    cardinality: f.cardinality ?? undefined,
    correlationCodeJsonPath: f.correlationCodeJsonPath ?? undefined,
    correlationCodeValue: f.correlationCodeValue ?? undefined,
    isEnabled: f.isEnabled,
  };
}

/** Reverse of toMappingRow — MappingRow -> MappingFieldDto for the save request. */
function toMappingFieldDto(row: MappingRow): MappingFieldDto {
  return {
    targetField: row.targetName,
    jsonPath: row.jsonPath ?? '',
    valueType: (row.valueType as MappingValueType) ?? 'String',
    isRequired: row.isRequired ?? row.isUpsertKey,
    defaultValue: row.defaultValue ?? null,
    format: row.format ?? null,
    normalizationType: row.normalizationType ?? null,
    terminologySystemJsonPath: row.terminologySystemJsonPath ?? null,
    terminologyCodeJsonPath: row.terminologyCodeJsonPath ?? null,
    arrayPolicy: (row.arrayPolicy as ArrayPolicyType) ?? 'Scalar',
    cardinality: row.cardinality ?? null,
    arrayAncestors: row.arrays && row.arrays.length > 0 ? row.arrays : null,
    isUpsertKey: row.isUpsertKey,
    correlationCodeJsonPath: row.correlationCodeJsonPath ?? null,
    correlationCodeValue: row.correlationCodeValue ?? null,
    isEnabled: row.isEnabled ?? true,
  };
}

/**
 * Create/edit/view dialog for a single MappingProfile — embeds MappingProfileFormComponent for the field-level
 * editing (docs/backend/14-mapping-profile-master-screen-plan.md §5.1/§5.2), the same viewChild()+read-signals
 * pattern DestinationConnectionDialogComponent uses for its own embedded form. This screen is the ONE place the
 * advanced MappingFieldDto members (IsRequired, DefaultValue, ArrayPolicy, CorrelateByCode, ...) are reachable —
 * the destination wizard has no UI for them.
 *
 * No live schema probe here (unlike the wizard's SQL step, this dialog never has decrypted destination
 * credentials to test a connection with) — Destination Object is always free text, and MappingProfileFormComponent
 * renders every destination type's mapping table in its no-live-schema fallback mode.
 */
@Component({
  selector: 'app-mapping-profile-dialog-v2',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatProgressSpinnerModule, MappingProfileCanvasComponent,
  ],
  templateUrl: './mapping-profile-dialog-v2.component.html',
  styleUrl: './mapping-profile-dialog-v2.component.scss',
})
export class MappingProfileDialogV2Component {
  private readonly fb = inject(FormBuilder);
  private readonly http = inject(HttpClient);
  private readonly svc = inject(MappingProfileService);
  private readonly schemaSvc = inject(DestinationSchemaService);
  readonly dialogRef = inject<DialogRef<MappingProfileDto | false>>(DialogRef);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly data = inject<MappingProfileDialogV2Data>(DIALOG_DATA) as MappingProfileDialogV2Data;

  readonly mappingForm = viewChild(MappingProfileCanvasComponent);

  readonly mode = this.data.mode;
  readonly isCreate = this.mode === 'create';
  readonly isView = this.mode === 'view';
  readonly isEdit = this.mode === 'edit';

  // The dropdown used to be scoped to FHIR_RESOURCES (the MVP1 11-resource subset the source-connection
  // scope picker still uses) — Mapping Profiles has no such scoping reason to hide the rest of what the
  // backend actually catalogs a template for, so this offers every resource in SUPPORTED_RESOURCE_TYPES
  // (kept in sync with SupportedFhirResourceTypes.All) instead.
  readonly resourceTypeOptions = SUPPORTED_RESOURCE_TYPES;
  readonly sourceOptions = signal<NamedEntity[]>([]);
  readonly destinationOptions = signal<NamedEntity[]>([]);

  readonly metaForm = this.fb.group({
    name: [this.data.mappingProfile?.name ?? '', [Validators.required]],
    resourceType: [this.data.mappingProfile?.resourceType ?? '', [Validators.required]],
    sourceConnectionId: [this.data.mappingProfile?.sourceConnectionId ?? '', [Validators.required]],
    destinationId: [this.data.mappingProfile?.destinationId ?? '', [Validators.required]],
    destinationObject: [this.data.mappingProfile?.destinationObject ?? '', [Validators.required]],
    isEnabled: [this.data.mappingProfile?.isEnabled ?? true],
  });

  // Reactive-forms values aren't signals — reading metaForm.controls.destinationId.value directly inside a
  // computed() would never re-trigger it on a later selection change (computed only tracks signal reads taken
  // during its own evaluation, and a plain FormControl getter isn't one). toSignal() bridges valueChanges into
  // a real signal so selectedDestinationType/destType actually update when the dropdown changes.
  private readonly destinationIdValue = toSignal(this.metaForm.controls.destinationId.valueChanges, {
    initialValue: this.metaForm.controls.destinationId.value,
  });

  readonly selectedDestinationType = computed<DestinationType | undefined>(() =>
    this.destinationOptions().find(d => d.id === this.destinationIdValue())?.destinationType,
  );
  readonly destType = computed(() => toDestKind(this.selectedDestinationType()));

  /** Human-readable label for the header's destination-type badge — a handful of common types get a real
   *  display name (matching how the destination wizard itself labels them); anything else (Snowflake,
   *  BlobStorage, ...) falls back to space-separating the PascalCase DestinationType value itself rather
   *  than needing an entry for all 22. */
  private static readonly DEST_TYPE_LABELS: Partial<Record<DestinationType, string>> = {
    SqlServer: 'SQL Server', AzureSql: 'Azure SQL', MySql: 'MySQL', PostgreSql: 'PostgreSQL',
    Mongo: 'MongoDB', Csv: 'CSV', Sftp: 'SFTP', RestApi: 'REST API', FhirRepository: 'FHIR Repository',
  };
  readonly destTypeLabel = computed<string | null>(() => {
    const t = this.selectedDestinationType();
    if (!t) return null;
    return MappingProfileDialogV2Component.DEST_TYPE_LABELS[t] ?? t.replace(/([a-z])([A-Z])/g, '$1 $2');
  });

  /** Live field-mapping count for the header badge — reads straight off the embedded canvas's own row
   *  state (already public, see MappingProfileCanvasComponent.rowsRich) rather than duplicating it here. */
  readonly mappingCount = computed(() => this.mappingForm()?.rowsRich().length ?? 0);

  /** Live tables/columns of the selected destination, introspected server-side from its stored secret — empty
   *  for a non-relational destination type or before one is selected. Feeds MappingProfileFormComponent's
   *  [sqlTables] so the SQL column pickers show real columns instead of always falling back to free text. */
  readonly sqlTables = signal<DestinationTable[]>([]);
  // Bumped by every _loadSchemaFor() call, captured as `seq` in its own closure — an in-flight getSchema()
  // response only ever gets applied if it's still the most recent request when it lands. Without this, picking
  // destination A then quickly switching to B starts two overlapping requests with no cancellation; if A's
  // (now-stale) response happens to resolve after B's, it would silently overwrite sqlTables() with A's tables
  // even though B is the current selection — an intermittent, network-timing-dependent race, not a logic bug
  // tied to any one destination, which is exactly why it only ever showed up "sometimes".
  private _schemaRequestSeq = 0;

  readonly initialRows = computed<MappingRow[]>(() => {
    const profile = this.data.mappingProfile;
    if (!profile) return [];
    return profile.fields.map(f => toMappingRow(profile.resourceType, profile.destinationObject, f));
  });

  /** Seeds MappingProfileFormComponent's per-resource table/target picker with whatever Destination Object
   *  already holds (edit mode's saved value), so the picker starts aligned with the meta field instead of blank. */
  readonly initialTargets = computed<Record<string, string>>(() => {
    const profile = this.data.mappingProfile;
    return profile ? { [profile.resourceType]: profile.destinationObject } : {};
  });

  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  /** Per-row problems found by validateMappingForSave, rendered as a blocking list right above the
   *  canvas — kept separate from errorMessage (a single generic line) since there can be several of
   *  these at once and each names the exact field/table at fault. */
  readonly mappingErrors = signal<string[]>([]);

  constructor() {
    this.http.get<NamedEntity[]>(SOURCE_CONNECTIONS_ENDPOINTS.list).subscribe({
      next: sources => this.sourceOptions.set(sources),
      error: () => this.sourceOptions.set([]),
    });
    this.http.get<NamedEntity[]>(DESTINATION_ENDPOINTS.list).subscribe({
      next: destinations => this.destinationOptions.set(destinations),
      error: () => this.destinationOptions.set([]),
    });

    if (this.isView) {
      this.metaForm.disable({ emitEvent: false });
    }

    effect(() => {
      const destinationId = this.destinationIdValue();
      untracked(() => this._loadSchemaFor(destinationId));
    });
  }

  private _loadSchemaFor(destinationId: string | null | undefined): void {
    const seq = ++this._schemaRequestSeq;
    if (!destinationId) {
      this.sqlTables.set([]);
      return;
    }
    this.schemaSvc.getSchema(destinationId).subscribe({
      // A newer call (a later destination pick) started while this one was still in flight — that call owns
      // sqlTables() now, whether or not it has resolved yet; applying this stale response would silently
      // revert it to the wrong destination's tables.
      next: res => { if (seq === this._schemaRequestSeq) this.sqlTables.set(res.tables); },
      error: () => { if (seq === this._schemaRequestSeq) this.sqlTables.set([]); },
    });
  }

  // The mapping canvas's own rows aren't individually dirty-tracked (each row's fields can change
  // without the row count changing) — a row-count difference from the seeded baseline (initialRows) is
  // a cheap, honest proxy that catches "added/removed a mapped field" but not "edited a field already
  // mapped without adding/removing any row." metaForm.dirty covers the header fields (name, resource
  // type, source/destination, enabled) fully and exactly.
  hasUnsavedChanges(): boolean {
    if (this.metaForm.dirty) return true;
    const currentCount = this.mappingForm()?.rowsRich().length ?? 0;
    return currentCount !== this.initialRows().length;
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    // Defense-in-depth: MappingProfileListComponent.openNew()/openEdit() already checked before this
    // dialog opened — re-checked here against a permission change landing mid-edit.
    const requiredCode = this.isCreate ? 'mappingprofiles.create' : 'mappingprofiles.edit';
    if (!this.actionGuard.ensure(requiredCode, `You do not have permission to ${this.isCreate ? 'create' : 'edit'} mapping profiles.`)) return;
    this.errorMessage.set(null);
    this.mappingErrors.set([]);

    if (this.metaForm.invalid) {
      this.metaForm.markAllAsTouched();
      return;
    }

    const rows = this.mappingForm()?.getRows() ?? [];
    if (rows.length === 0) {
      this.errorMessage.set('Map at least one field before saving.');
      return;
    }

    const mappingErrors = this.validateMappingForSave();
    if (mappingErrors.length > 0) {
      this.mappingErrors.set(mappingErrors);
      return;
    }

    const v = this.metaForm.getRawValue();
    // For a SQL-family destination, MappingProfileFormComponent's own per-resource table dropdown (fed by
    // sqlTables()) is the authoritative pick — it can differ from whatever is still typed in the Destination
    // Object field above if the user picked a table there instead of editing that field directly.
    const pickedTarget = this.mappingForm()?.targetByResource()[v.resourceType!];
    const request: CreateMappingProfileRequest = {
      name: v.name!,
      resourceType: v.resourceType!,
      sourceConnectionId: v.sourceConnectionId!,
      destinationId: v.destinationId!,
      destinationObject: pickedTarget || v.destinationObject!,
      fields: rows.map(toMappingFieldDto),
    };

    this.saving.set(true);
    const action = this.isCreate
      ? this.svc.create(request)
      : this.svc.update(this.data.mappingProfile!.id, request);

    action.subscribe({
      next: result => {
        this.saving.set(false);
        this.dialogRef.close(result);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.message ?? 'Failed to save the mapping profile.');
      },
    });
  }

  /** Mirrors DestinationWizardComponent.validateMappingForSave — the same class of mistakes (a target
   *  row never actually wired to a source, a row left pointed at a table/column the live schema no
   *  longer has, a row targeting an identity/computed or foreign-key column, two fields both writing the
   *  same column) checked there before its own canvas's "Save" is allowed through. This dialog hosts the
   *  identical FieldMappingCanvasComponent (see
   *  MappingProfileCanvasComponent's own doc comment) so it's exposed to exactly the same failure modes,
   *  just with no equivalent check of its own until now. Reads the canvas's rich rows (rowsRich(), not
   *  getRows()'s already-flattened output) because "no source wired" and "no source group picked" are
   *  properties of the rich row's mode/sources/childNodeId — information serializeRowsFlat has already
   *  collapsed away by the time getRows() returns. */
  private validateMappingForSave(): string[] {
    const errors: string[] = [];
    const rows = this.mappingForm()?.rowsRich() ?? [];
    if (rows.length === 0) return errors;

    const isSqlFamily = isSqlFamilyDestType(this.destType());
    const tables = this.sqlTables();
    const knownTables = isSqlFamily && tables.length > 0 ? new Set(tables.map(t => t.fullName)) : null;
    const targetCounts = new Map<string, number>();

    for (const row of rows) {
      const label = row.targetName || '(unnamed field)';
      const table = row.tableName || 'the destination';

      if (row.mode === 'value' && row.sources.length === 0) {
        errors.push(`"${label}" on ${table} has no source field selected.`);
        continue;
      }
      if (row.mode === 'childJson' && !row.childNodeId) {
        errors.push(`"${label}" on ${table} has no source group selected.`);
        continue;
      }
      if (!row.targetName) {
        errors.push(`A mapped field on ${table} has no destination column selected.`);
        continue;
      }

      if (knownTables && !knownTables.has(row.tableName)) {
        errors.push(`${row.tableName} no longer exists in the destination database — remove or retarget "${label}".`);
        continue;
      }
      if (knownTables) {
        const schemaTable = tables.find(t => t.fullName === row.tableName);
        const schemaColumn = schemaTable?.columns.find(c => c.name === row.targetName);
        if (schemaTable && !schemaColumn) {
          errors.push(`"${label}" no longer exists in ${row.tableName}'s columns.`);
          continue;
        }
        // Belt-and-suspenders for a row wired before FieldMappingCanvasComponent.completeMapping
        // started guarding against these (or restored from an older/legacy snapshot) — the column's
        // value is filled in automatically regardless (by the database for isAutoGenerated, by the
        // mapping engine's own child-table relationship resolution for isForeignKey), so a direct
        // write to it is never valid.
        if (schemaColumn?.isAutoGenerated) {
          errors.push(`"${label}" is an identity or computed column in ${row.tableName} and cannot be a mapping write target — remove or retarget this field.`);
          continue;
        }
        if (schemaColumn?.isForeignKey) {
          errors.push(`"${label}" is a foreign key on ${row.tableName}${schemaColumn.references ? ` (→ ${schemaColumn.references})` : ''} — it's populated automatically from that relationship and cannot be a mapping write target. Remove or retarget this field.`);
          continue;
        }
        // Reuses the exact same check field-mapping-model.ts's own Map Fields screen and
        // CreateMappingProfileRequestValidator.ValidateAgainstDestinationSchemaAsync (the backend's
        // /workflows/build validator) already enforce — was previously a hand-rolled copy here that
        // additionally, incorrectly exempted every childJson row outright (a JSON-shaped row IS a real
        // type — "always written as JSON text" doesn't mean any destination column can hold it; see
        // checkColumnTypeCompatibility's own doc comment). Reusing the shared function instead of a
        // second inline copy is what keeps this dialog's own pre-save check, the canvas's, and the
        // backend's genuinely in sync going forward, rather than three copies free to drift apart again.
        const typeError = checkColumnTypeCompatibility(row, schemaColumn);
        if (typeError) {
          errors.push(typeError);
          continue;
        }
      }

      const key = `${row.tableName}::${row.targetName}`;
      targetCounts.set(key, (targetCounts.get(key) ?? 0) + 1);
    }

    for (const [key, count] of targetCounts) {
      if (count <= 1) continue;
      const [tableName, targetName] = key.split('::');
      errors.push(`${tableName}.${targetName} is mapped ${count} times — only one mapping would actually be written.`);
    }

    return errors;
  }
}
