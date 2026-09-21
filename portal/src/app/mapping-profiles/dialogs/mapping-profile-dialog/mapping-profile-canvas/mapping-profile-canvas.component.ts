import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import {
  ResourceFieldDef, DEST_RESOURCE_DEFS, genericResourceDef, MappingRow as SimpleMappingRow,
} from '../../../../components/node-library/destination-wizard/mapping-profile-form.component';
import { MappingCatalogService, FhirElement } from '../../../../services/mapping-catalog.service';
import { DestinationColumn, DestinationTable, DestinationProbeRequest } from '../../../../services/destination-schema.service';
import { FieldMappingCanvasComponent } from '../../../../components/node-library/destination-wizard/field-mapping/field-mapping-canvas.component';
import {
  MappingRow, MappingSourceRef, MappingDestType, isSqlFamilyDestType, serializeRowsFlat,
} from '../../../../components/node-library/destination-wizard/field-mapping/field-mapping-model';
import { ChildTableRelation } from '../../../../components/node-library/destination-wizard/field-mapping/field-mapping-summary.model';

/** Advanced MappingFieldDto members the canvas (same as the wizard's own — see field-mapping-model.ts's
 *  MappingRow doc comment) has no widgets for, but which an admin may have set on a previously-saved
 *  profile through the OLD flat table (the only UI that ever exposed them). Carried on the side, keyed
 *  by column name, and merged back in at save time so re-editing through this canvas can never silently
 *  drop them just because the canvas has nothing to display them with. */
interface AdvancedFields {
  isRequired?: boolean;
  defaultValue?: string;
  format?: string;
  normalizationType?: string;
  terminologySystemJsonPath?: string;
  terminologyCodeJsonPath?: string;
  cardinality?: string;
  correlationCodeJsonPath?: string;
  correlationCodeValue?: string;
  isEnabled?: boolean;
}

/**
 * Hosts the Destination Wizard's own field-mapping canvas (`FieldMappingCanvasComponent`) inside the
 * Mapping Profiles dialog, in place of the flat "Add Fields / + Add field / Clear Fields" table
 * (MappingProfileFormComponent). This is a thin data-plumbing layer — the actual UI (source tree,
 * target cards, SVG wires, join popover, mapping list, preview drawer, load-payload/create-table/
 * add-column/edit-column modals) all live inside FieldMappingCanvasComponent's own template; this class
 * just derives what it needs from this dialog's simpler inputs and reconciles a couple of invariants
 * the wizard's canvas has no equivalent of.
 *
 * The one capability deliberately NOT wired through is live schema authoring (create table / add
 * column / drop column / alter column) — this dialog only ever has a destinationId + a read-only schema
 * probe, never decrypted credentials, so `connectionInfo` is always null and
 * `schemaAuthoringEnabled="false"` hides every entry point that would otherwise silently no-op against
 * it (see FieldMappingCanvasComponent.schemaAuthoringEnabled's own doc comment).
 *
 * Two invariants the wizard's canvas has no concept of, preserved here at the host level instead of by
 * forking the shared component:
 *  - The resource's `id` field must always be mapped and always be the upsert key (mirrors
 *    MappingProfileFormComponent._reconcileIdRows) — self-heals via the `rowsRich`-dependent effect
 *    below if the user unmaps/retargets it through the canvas's own delete/drag controls.
 *  - Advanced MappingFieldDto members (isRequired, defaultValue, format, normalizationType, terminology*,
 *    cardinality, correlationCode*, isEnabled) — the canvas has no UI for these (same gap the wizard
 *    always had; this Mapping Profiles screen was the one place they were ever author-able, per
 *    docs/backend/14-mapping-profile-master-screen-plan.md §3.2) — carried on the side and merged back
 *    into getRows()'s output so re-editing an existing profile through this canvas can't silently erase
 *    them.
 *
 * Public surface (resources/destType/sqlTables/initialRows/initialTargets/readOnly in, getRows()/
 * targetByResource() out) mirrors MappingProfileFormComponent's, so MappingProfileDialogComponent needed
 * no logic changes beyond the template swap.
 */
@Component({
  selector: 'app-mapping-profile-canvas',
  standalone: true,
  imports: [FieldMappingCanvasComponent],
  templateUrl: './mapping-profile-canvas.component.html',
  styleUrl: './mapping-profile-canvas.component.scss',
})
export class MappingProfileCanvasComponent {
  private readonly catalogSvc = inject(MappingCatalogService);

  readonly resources = input.required<string[]>();
  readonly destType = input.required<MappingDestType>();
  readonly sqlTables = input<DestinationTable[]>([]);
  readonly initialRows = input<SimpleMappingRow[]>([]);
  readonly initialTargets = input<Record<string, string>>({});
  readonly readOnly = input(false);

  readonly resource = computed(() => this.resources()[0] ?? '');
  // Shared predicate, not an inline list — Masters and the workflow canvas must agree about which
  // destinations are relational, and Fabric Warehouse is now one of them.
  readonly isSql = computed(() => isSqlFamilyDestType(this.destType()));

  // No decrypted destination credentials are ever available here — see this class's own doc comment.
  readonly connectionInfo: DestinationProbeRequest | null = null;
  readonly schemaAuthoringEnabled = false;
  readonly noChildTableRelations: Record<string, ChildTableRelation> = {};

  // ── destination target (primary table/file) + extra tables ────────────────
  readonly targetByResource = signal<Record<string, string>>({});
  target = computed(() => this.targetByResource()[this.resource()] ?? '');

  onTargetChange(value: string): void {
    if (this.readOnly()) return;
    this.targetByResource.update(m => ({ ...m, [this.resource()]: value }));
  }

  /** Extra tables added alongside the primary target — picked from already-probed real tables only
   *  (schemaAuthoringEnabled is false, so "Create a new table…" never reaches here). */
  readonly extraTables = signal<string[]>([]);

  onExtraTablesChange(tables: string[]): void {
    if (this.readOnly()) return;
    const removed = this.extraTables().filter(t => !tables.includes(t));
    this.extraTables.set(tables);
    if (removed.length) {
      this.rowsRich.update(rows => rows.filter(r => !(r.resource === this.resource() && removed.includes(r.tableName))));
    }
  }

  hasSqlTables(): boolean {
    return this.isSql() && this.sqlTables().length > 0;
  }

  sqlTableOptions(): string[] {
    return this.sqlTables().map(t => t.fullName);
  }

  private tableForTarget(): DestinationTable | undefined {
    const target = this.target();
    return this.sqlTables().find(t => t.fullName === target || t.tableName === target);
  }

  // Deliberately unfiltered (identity/computed and foreign-key columns included) — this feeds the
  // canvas's own target-card rendering, which should still show a table's true shape, PK/FK badges
  // included. FieldMappingCanvasComponent.isProtectedColumn is what actually stops one of these from
  // becoming a mapping target, checked at the point a mapping is completed instead of here.
  columnsForResourceTargetFn = (): string[] =>
    (this.tableForTarget()?.columns ?? []).map(c => c.name);

  columnsForTableFn = (tableFullName: string): string[] => {
    const table = this.sqlTables().find(t => t.fullName === tableFullName);
    return table ? table.columns.map(c => c.name) : [];
  };

  dataTypeForTableFn = (tableFullName: string, column: string): string | undefined =>
    this.sqlTables().find(t => t.fullName === tableFullName)?.columns.find(c => c.name === column)?.dataType;

  keyInfoForTableFn = (tableFullName: string, column: string): DestinationColumn | undefined =>
    this.sqlTables().find(t => t.fullName === tableFullName)?.columns.find(c => c.name === column);

  /** Already-probed tables not yet used as this resource's primary or extra targets. */
  availableTablesToAddFn = (): string[] => {
    const used = new Set([this.target(), ...this.extraTables()]);
    return this.sqlTables().map(t => t.fullName).filter(t => !used.has(t));
  };

  allColumnsAutoGenerated(): boolean {
    const columns = this.tableForTarget()?.columns ?? [];
    return columns.length > 0 && columns.every(c => c.isAutoGenerated);
  }

  // ── FHIR field catalog (array-aware backend catalog > a loaded JSON payload's own shape > the
  // built-in defs) ────────────────────────────────────────────────────────────────────────────────────
  private readonly catalogByResource = signal<Record<string, ResourceFieldDef[]>>({});
  private readonly payloadFieldsByResource = signal<Record<string, ResourceFieldDef[]>>({});
  private readonly _requested = new Set<string>();

  private defFor(r: string): ResourceFieldDef[] {
    return (DEST_RESOURCE_DEFS[r] ?? genericResourceDef(r)).fields;
  }

  availableFields(r: string): ResourceFieldDef[] {
    return this.payloadFieldsByResource()[r] ?? this.catalogByResource()[r] ?? this.defFor(r);
  }

  readonly availableFieldsFn = (r: string): ResourceFieldDef[] => this.availableFields(r);

  /** Same precedence as availableFields() minus its pasted-payload override — see
   *  DestinationWizardComponent.defaultAvailableFields's own doc comment for why this is needed
   *  separately (the Load JSON Payload modal's "Reset to Original" must restore this, not whatever
   *  override happens to be active right now). */
  defaultAvailableFields(r: string): ResourceFieldDef[] {
    return this.catalogByResource()[r] ?? this.defFor(r);
  }

  readonly defaultAvailableFieldsFn = (r: string): ResourceFieldDef[] => this.defaultAvailableFields(r);

  private ensureCatalog(resource: string): void {
    if (this._requested.has(resource)) return;
    this._requested.add(resource);
    this.catalogSvc.fields(resource).subscribe((fields: FhirElement[]) => {
      if (!fields.length) { this._requested.delete(resource); return; }
      const defs = fields.map(f => this.toFieldDef(resource, f));
      this.catalogByResource.update(m => ({ ...m, [resource]: defs }));
    });
  }

  private toFieldDef(resource: string, f: FhirElement): ResourceFieldDef {
    const column = f.fhirPath.split('.').map(s => s.charAt(0).toUpperCase() + s.slice(1)).join('');
    return {
      label: f.label || f.fhirPath,
      path: `${resource}.${f.fhirPath}`,
      sqlColumn: column,
      csvColumn: column,
      jsonPath: f.jsonPath,
      valueType: f.valueType,
      arrays: f.arrays,
      referenceTargetTypes: f.referenceTargetTypes,
    };
  }

  onSourcePayloadLoaded(e: { resource: string; fields: ResourceFieldDef[] }): void {
    if (this.readOnly()) return;
    this.payloadFieldsByResource.update(m => ({ ...m, [e.resource]: e.fields }));
  }

  /** "Reset to Original" in the Load JSON Payload modal — see DestinationWizardComponent.
   *  onSourcePayloadReset's own doc comment; identical reasoning/behavior here. */
  onSourcePayloadReset(resource: string): void {
    if (this.readOnly()) return;
    this.payloadFieldsByResource.update(m => {
      const rest = { ...m };
      delete rest[resource];
      return rest;
    });
  }

  // ── mapping rows (rich model — the canvas's own MappingRow, with joins/childJson/instance-selection
  // support none of the flat table it replaces ever had) ─────────────────────────────────────────────
  readonly rowsRich = signal<MappingRow[]>([]);
  /** Advanced MappingFieldDto members with no canvas UI — see this class's own doc comment. */
  private readonly advancedByColumn = signal<Record<string, AdvancedFields>>({});

  private toRichRow(row: SimpleMappingRow): MappingRow {
    const source: MappingSourceRef = {
      fhirPath: row.fhirPath, label: row.fieldLabel, jsonPath: row.jsonPath, valueType: row.valueType, arrays: row.arrays,
    };
    return {
      resource: row.resource, sources: [source], mode: 'value', instance: { type: 'first' },
      targetName: row.targetName, tableName: row.tableName, isUpsertKey: row.isUpsertKey,
    };
  }

  onMappingRowsChange(rows: MappingRow[]): void {
    if (this.readOnly()) return;
    this.rowsRich.set(rows);
  }

  onTargetByResourceChange(v: Record<string, string>): void {
    if (this.readOnly()) return;
    this.targetByResource.set(v);
  }

  /** Mirrors MappingProfileFormComponent.getRows() — the dialog reads this back out at save time.
   *  Reuses field-mapping-model.ts's own serializeRowsFlat (the same function the wizard's own save
   *  path uses) as the single source of truth for join/childJson/array-policy flattening, rather than
   *  re-deriving that logic here. */
  getRows(): SimpleMappingRow[] {
    const advanced = this.advancedByColumn();
    return serializeRowsFlat(this.rowsRich(), this.targetByResource(), this.sqlTables(), {}).map(r => {
      const adv = advanced[r.column];
      return {
        resource: r.resource,
        fieldLabel: r.field,
        fhirPath: r.path,
        targetName: r.column,
        tableName: r.target,
        jsonPath: r.jsonPath,
        valueType: r.valueType,
        arrays: r.arrays,
        isUpsertKey: r.isUpsertKey,
        arrayPolicy: r.arrayPolicy,
        isRequired: adv?.isRequired,
        defaultValue: adv?.defaultValue,
        format: adv?.format,
        normalizationType: adv?.normalizationType,
        terminologySystemJsonPath: adv?.terminologySystemJsonPath,
        terminologyCodeJsonPath: adv?.terminologyCodeJsonPath,
        cardinality: adv?.cardinality,
        correlationCodeJsonPath: adv?.correlationCodeJsonPath,
        correlationCodeValue: adv?.correlationCodeValue,
        isEnabled: adv?.isEnabled,
      };
    });
  }

  constructor() {
    // Seed once from whatever the dialog passed in (a saved profile's fields, on edit/view).
    effect(() => {
      const rows = this.initialRows();
      const targets = this.initialTargets();
      untracked(() => {
        if (rows.length) {
          this.rowsRich.set(rows.map(r => this.toRichRow(r)));
          const advanced: Record<string, AdvancedFields> = {};
          for (const r of rows) {
            advanced[r.targetName] = {
              isRequired: r.isRequired, defaultValue: r.defaultValue, format: r.format,
              normalizationType: r.normalizationType, terminologySystemJsonPath: r.terminologySystemJsonPath,
              terminologyCodeJsonPath: r.terminologyCodeJsonPath, cardinality: r.cardinality,
              correlationCodeJsonPath: r.correlationCodeJsonPath, correlationCodeValue: r.correlationCodeValue,
              isEnabled: r.isEnabled,
            };
          }
          this.advancedByColumn.set(advanced);
        }
        if (Object.keys(targets).length) this.targetByResource.set(targets);
      });
    });

    effect(() => { this.ensureCatalog(this.resource()); });

    // Keeps the resource's mandatory id/upsert-key row in sync — inserted the moment a target table is
    // chosen, upgraded to the schema-verified PK/unique column once columns load, and self-healed if the
    // user unmaps/retargets it through the canvas's own delete/drag controls (rowsRich() is an explicit
    // dependency here for exactly that reason). Mirrors MappingProfileFormComponent._reconcileIdRows,
    // trimmed to the single resource this dialog ever maps.
    effect(() => {
      this.resource();
      this.destType();
      this.catalogByResource();
      this.sqlTables();
      this.targetByResource();
      this.rowsRich();
      untracked(() => this.reconcileIdRow());
    });

    // Auto-fit used to run here automatically (once on first content, once again after the real FHIR
    // catalog replaced the fallback field list) — removed because it fought the fixed 80% default this
    // screen now opens at (see field-mapping-canvas.component's initialZoom input): the deliberate 80%
    // would apply, then get silently overwritten a moment later once the catalog loaded and this fired.
    // The manual "⤢ Fit to view" button (bumpZoomFit(), still below) is unaffected — only the automatic,
    // on-load firing is gone. A resource with dozens of fields (Practitioner: 70+) may still need a
    // manual zoom-out or fit-to-view click to see everything at once.
  }

  private isIdRow(row: MappingRow): boolean {
    return row.mode === 'value' && row.sources[0]?.fhirPath === `${row.resource}.id`;
  }

  private idFieldFor(r: string): ResourceFieldDef | undefined {
    return this.availableFields(r).find(f => f.path === `${r}.id`);
  }

  private autoMatchIdColumn(): string | null {
    const columns = this.tableForTarget()?.columns ?? [];
    return columns.find(c => c.isPrimaryKey && !c.isAutoGenerated)?.name
      ?? columns.find(c => c.isUnique && !c.isAutoGenerated)?.name
      ?? null;
  }

  private isAutoGeneratedColumn(columnName: string): boolean {
    if (!columnName) return false;
    return (this.tableForTarget()?.columns ?? []).some(c => c.name === columnName && !!c.isAutoGenerated);
  }

  private reconcileIdRow(): void {
    const r = this.resource();
    if (!r) return;
    // SQL: nothing should populate until a real table is actually chosen — otherwise the mandatory id
    // row shows up against no real table at all.
    if (this.isSql() && !this.target()) return;

    const idField = this.idFieldFor(r);
    if (!idField) return;

    const autoColumn = this.autoMatchIdColumn();
    const rows = this.rowsRich();
    const idx = rows.findIndex(row => row.resource === r && this.isIdRow(row));
    const source: MappingSourceRef = {
      fhirPath: idField.path, label: idField.label, jsonPath: idField.jsonPath, valueType: idField.valueType, arrays: idField.arrays,
    };

    if (idx === -1) {
      const initialColumn = autoColumn ?? (this.isSql() ? idField.sqlColumn : idField.csvColumn);
      this.rowsRich.set([
        { resource: r, sources: [source], mode: 'value', instance: { type: 'first' }, targetName: initialColumn, tableName: this.target(), isUpsertKey: true },
        ...rows,
      ]);
      return;
    }

    const row = rows[idx];
    const savedIsAutoGenerated = this.isAutoGeneratedColumn(row.targetName);
    const desiredColumn = autoColumn ?? (savedIsAutoGenerated ? '' : row.targetName);
    const desiredTable = row.tableName || this.target();
    if (row.targetName !== desiredColumn || row.tableName !== desiredTable || !row.isUpsertKey || row.sources[0]?.jsonPath !== idField.jsonPath) {
      const next = [...rows];
      next[idx] = { ...row, targetName: desiredColumn, tableName: desiredTable, isUpsertKey: true, sources: [source] };
      this.rowsRich.set(next);
    }
  }

  // ── toolbar trigger counters (Suggest mappings / Load payload / Preview / zoom) — same
  // bump-a-counter-the-canvas-watches pattern NodeLibraryDialogComponent uses to relocate this exact
  // chrome out of the canvas's own floating controls into a host-owned toolbar. ─────────────────────
  readonly runSuggestMappingsTrigger = signal(0);
  readonly clearSuggestionsTrigger = signal(0);
  readonly openLoadPayloadTrigger = signal(0);
  readonly zoomInTrigger = signal(0);
  readonly zoomOutTrigger = signal(0);
  readonly zoomResetTrigger = signal(0);
  readonly zoomFitTrigger = signal(0);

  bumpRunSuggestMappings(): void { this.runSuggestMappingsTrigger.update(v => v + 1); }
  bumpClearSuggestions(): void { this.clearSuggestionsTrigger.update(v => v + 1); }
  bumpLoadPayload(): void { this.openLoadPayloadTrigger.update(v => v + 1); }
  bumpZoomIn(): void { this.zoomInTrigger.update(v => v + 1); }
  bumpZoomOut(): void { this.zoomOutTrigger.update(v => v + 1); }
  bumpZoomReset(): void { this.zoomResetTrigger.update(v => v + 1); }
  bumpZoomFit(): void { this.zoomFitTrigger.update(v => v + 1); }

  readonly suggestionCount = signal(0);
  readonly zoomPercentDisplay = signal('80%');
}
