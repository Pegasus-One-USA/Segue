import {
  Component, ElementRef, HostListener, computed, effect, inject, input, output, signal, viewChild, AfterViewInit, OnDestroy, OnInit,
} from '@angular/core';
import type { ResourceFieldDef } from '../destination-wizard.component';
import { MappingRow, MappingSourceRef, MappingInstanceSelection, isApproximated, PendingSchemaOp, MappingDestType, SchemaLoadState, qualifyTableName, splitTableName } from './field-mapping-model';
import { canQueueAddColumn, describeCreateTableConflict, describeLiveCreateTableConflict } from './field-mapping-schema-ops.util';
import { FmTreeNode, buildForest, findNode } from './field-mapping-tree.util';
import { MappingSuggestion, suggestMappings } from './field-mapping-automap.util';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { FieldMappingSourceTreeComponent } from './field-mapping-source-tree.component';
import { FieldMappingTargetCardComponent } from './field-mapping-target-card.component';
import { FieldMappingWiresComponent, FmTempWire } from './field-mapping-wires.component';
import { FmDragStart, FmDragMove, FmDragEnd, FmFieldClick } from './field-mapping-tree-node.component';
import { FieldMappingListComponent } from './field-mapping-list.component';
import { FieldMappingJoinPopoverComponent } from './field-mapping-join-popover.component';
import { FieldMappingPreviewDrawerComponent } from './field-mapping-preview-drawer.component';
import { FieldMappingAddColumnModalComponent, FmAddColumnSubmit } from './field-mapping-add-column-modal.component';
import { FieldMappingEditColumnModalComponent, FmEditColumnSubmit } from './field-mapping-edit-column-modal.component';
import { FieldMappingCreateTableModalComponent, FmCreateTableSubmit } from './field-mapping-create-table-modal.component';
import { FieldMappingLoadPayloadModalComponent } from './field-mapping-load-payload-modal.component';
import { FieldMappingDefaultValueModalComponent, FmDefaultValueSubmit } from './field-mapping-default-value-modal.component';
import { FieldMappingLoadDestinationPayloadModalComponent } from './field-mapping-load-destination-payload-modal.component';
import { parseSourcePayloadJson, reconstructPayloadJsonFor, parseDestinationPayloadJson } from './field-mapping-payload.util';
import { ChildTableRelation } from './field-mapping-summary.model';
import { ToastService } from '../../../../services/toast.service';
import { DestinationColumn, DestinationTable, DestinationProbeRequest, DestinationSchemaService } from '../../../../services/destination-schema.service';
import { DestinationTypeV2 as DestinationType, DeIdentificationProfileDto } from '../../../../models/destination-configuration-v2.model';

export interface FmTargetCardSpec {
  resource: string;
  tableName: string;
  isExtra: boolean;
}

/** One selectable option in the "Select mapping" popover (see fieldPickerState) — the exact identity a
 *  MappingRow is already keyed by everywhere else in this file (resource/tableName/targetName), plus
 *  display-only labels for the option's own "source → target" line. Never a new mapping identity of its
 *  own; picking one just sets popoverKey to {resource, tableName, targetName}, identical to what a
 *  connector-line click already does. */
export interface FmMappingChoice {
  resource: string;
  tableName: string;
  targetName: string;
  sourceLabel: string;
  targetLabel: string;
  /** The one source this option represents within its row's `sources[]` — null only for a childJson
   *  (whole-node-as-JSON) row, which has no individual source to single out. Carried through to
   *  popoverKey.focusSourceFhirPath when this option is picked, so the popover it opens shows only this
   *  one source → target relationship instead of the whole join. */
  sourceFhirPath: string | null;
}

/**
 * Orchestrates the visual field-mapping canvas that replaces the wizard's old flat Map Fields table.
 * Owns all drag/keyboard/popover/drawer UI state locally; the actual MappingRow[] / target-by-resource
 * data is owned by the parent DestinationWizardComponent and flows in as inputs, out as outputs — this
 * component never holds its own copy of record state, just how the user is currently interacting with it.
 *
 * A resource can now map into more than one destination table (e.g. Patient's primary dbo.Patient table
 * plus a dbo.PatientContact child table for its repeating Contact array), so a mapping's identity is
 * (resource, tableName, column) everywhere in this file — resource + column alone would collide if two
 * tables happened to share a column name.
 */
@Component({
  selector: 'app-field-mapping-canvas',
  standalone: true,
  imports: [
    FieldMappingSourceTreeComponent,
    FieldMappingTargetCardComponent,
    FieldMappingWiresComponent,
    FieldMappingListComponent,
    FieldMappingJoinPopoverComponent,
    FieldMappingPreviewDrawerComponent,
    FieldMappingAddColumnModalComponent,
    FieldMappingEditColumnModalComponent,
    FieldMappingCreateTableModalComponent,
    FieldMappingLoadPayloadModalComponent,
    FieldMappingDefaultValueModalComponent,
    FieldMappingLoadDestinationPayloadModalComponent,
  ],
  providers: [FieldMappingAnchorService],
  templateUrl: './field-mapping-canvas.component.html',
  styleUrl: './field-mapping-canvas.component.scss',
})
export class FieldMappingCanvasComponent implements OnInit, AfterViewInit, OnDestroy {
  /** Which Mapping-list tab to land on — forwarded from the V2 chain node that opened the wizard
   *  (Mapping / Transformation / De-identification node) straight through to FieldMappingListComponent. */
  readonly initialListTab = input<'mappings' | 'transformations' | 'deidentification'>('mappings');

  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly toast = inject(ToastService);
  // Only ever called for the one live "does this table already exist for real" check submitCreateTable
  // needs before queueing a CREATE TABLE — every other schema mutation (the actual create/add/drop/alter
  // DDL) still stays owned by DestinationWizardComponent's own pendingSchemaOps flush, unchanged. Reuses
  // the exact same singleton service (providedIn: 'root') and the exact same schema-preview endpoint the
  // Step 1 connection-test flow already calls — not a new/duplicate table-existence API.
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly canvasInner = viewChild.required<ElementRef<HTMLElement>>('canvasInner');
  private readonly viewport = viewChild.required<ElementRef<HTMLElement>>('viewport');
  // Not .required — only rendered while addTableMenuOpen() is true (see toggleAddTableMenu, which
  // focuses it manually once open instead of the `autofocus` attribute, which @angular-eslint/template/
  // no-autofocus disallows for the accessibility reasons in its own rule description).
  private readonly addTableSearchInput = viewChild<ElementRef<HTMLInputElement>>('addTableSearchInput');
  // Not .required — only rendered while fieldPickerState() is non-null (see activateFieldMappings).
  private readonly fieldPickerPanel = viewChild<ElementRef<HTMLElement>>('fieldPickerPanel');
  private resizeObserver: ResizeObserver | null = null;
  private viewportResizeObserver: ResizeObserver | null = null;

  readonly resources = input.required<string[]>();
  /** Every resource selected for this destination, NOT scoped down to the currently active mapping group
   *  (unlike `resources` above) — needed so a reference field's "Resolves to" picker can offer resources
   *  other than whichever one is presently being edited. */
  readonly allResources = input<string[]>([]);
  readonly destType = input.required<MappingDestType>();
  /** The real backend DestinationType (e.g. 'SqlServer', 'Mongo') for this destination — distinct from
   *  destType above (which is the coarser 'sql'/'csv'/'mongo' family used to drive UI branching). Threaded
   *  down to the join popover so it can load/save a transformation rule scoped to the right destination.
   *  Optional (not every host of this canvas — e.g. the Mapping Profiles dialog — has a resolved
   *  destination type on hand); the popover simply hides its transformation-rule section when absent. */
  readonly rulesDestinationType = input<DestinationType | null>(null);
  /** Forwarded to the join popover AND the list panel's Transformations tab, so both resolve rules against
   *  THIS workflow's own tier — see the join popover's own workflowId
   *  input. Null while the workflow is unsaved. */
  readonly workflowId = input<string | null>(null);
  // ── pass-through to the "De-identification" tab (field-mapping-list) — same shared state
  // DestinationWizardComponent's Step 1 picker owns; this canvas has no logic of its own here. ──────────
  readonly deIdentificationProfiles = input<DeIdentificationProfileDto[]>([]);
  readonly selectedDeIdentificationProfileId = input<string | null>(null);
  readonly newProfileName = input<string>('');
  readonly creatingProfile = input<boolean>(false);
  readonly selectedDeIdentificationProfileIdChange = output<string | null>();
  readonly newProfileNameChange = output<string>();
  readonly createDeIdentificationProfileRequested = output<void>();
  readonly mappingRows = input.required<MappingRow[]>();
  readonly targetByResource = input.required<Record<string, string>>();
  readonly availableFields = input.required<(r: string) => ResourceFieldDef[]>();
  /** Same shape as availableFields, but never blended with a pasted-payload override — the real backend
   *  FHIR catalog (or the built-in defs, until that catalog loads). See DestinationWizardComponent.
   *  defaultAvailableFields's own doc comment: this is what the Load JSON Payload modal's "Reset to
   *  Original" reconstructs from (originalPayloadJsonFor below), since availableFields() alone can't tell
   *  "the true original" apart from "whatever override happens to be active right now" once ANY payload
   *  has ever been loaded for this resource. */
  readonly defaultAvailableFields = input.required<(r: string) => ResourceFieldDef[]>();
  readonly columnsForResourceTarget = input.required<(r: string) => string[]>();
  readonly hasSqlTables = input.required<boolean>();
  readonly sqlTableOptions = input.required<string[]>();
  /** Bare table names (no schema/database prefix) of every already-probed SQL table — only meaningful for
   *  MySQL, whose live schema probe qualifies names with the connected database (e.g.
   *  "fhirbridge_output.Patient", see SqlDestinationSchemaService.ReadColumnsAsync) while a saved/reopened
   *  mapping only ever persists the bare table name (bareName(), field-mapping-summary.model.ts). Lets
   *  isPrimaryTargetValid/columnsForTable tolerate that bare-vs-qualified mismatch for MySQL specifically,
   *  without loosening SQL Server/PostgreSQL's strict fullName match (their dbo./public. schema genuinely
   *  matches what the live probe returns, so no such mismatch exists there). */
  readonly sqlTableNames = input<string[]>([]);
  readonly csvDelimiterKey = input<string>('comma');
  /** Toolbar-level search (dialog header, see NodeLibraryDialogComponent) — live text, forwarded
   *  straight through to both the payload source tree and every destination target card below, each of
   *  which mirrors it into its own local searchQuery so it drives their existing filtering pipelines. */
  readonly searchQuery = input<string>('');

  // Extra tables added alongside the resource's primary table — either picked from tables the SQL
  // probe already found, or (when typed as a new name) created for real via CreateTableAsync.
  readonly extraTables = input<string[]>([]);
  /** Table names any currently-queued (not-yet-flushed) schema op still references this session — see
   *  field-mapping-schema-ops.util.ts's computePendingTableNames. Distinguishes "already exists for real"
   *  from "only staged on this canvas so far", which is what "Create a new table…"/"Add column" need to
   *  agree on to avoid the exact contradiction this exists to fix (one correctly says a table doesn't
   *  exist yet via a live check, the other incorrectly refuses to create it because it's "already on the
   *  canvas"). */
  readonly pendingTableNames = input<ReadonlySet<string>>(new Set());
  readonly columnsForTable = input<(tableFullName: string) => string[]>(() => []);
  /** Real data type of one column on any already-known SQL table — undefined for CSV or free-text
   *  columns with no real schema behind them. Purely a display concern for each target card. */
  readonly dataTypeForTable = input<(tableFullName: string, column: string) => string | undefined>(() => undefined);
  /** Real PK/FK status of one column on any already-known SQL table — undefined for CSV or free-text
   *  columns with no real schema behind them. Same display-only role as dataTypeForTable. */
  readonly keyInfoForTable = input<(tableFullName: string, column: string) => DestinationColumn | undefined>(() => undefined);
  /** Parent/PK/FK relation for any table created as a child of another (see ChildTableRelation) — keyed
   *  by table full name, owned by the wizard so it survives navigating between resources. Read-only here:
   *  a card just displays it, same display-only role as dataTypeForTable/keyInfoForTable. */
  readonly childTableRelations = input<Record<string, ChildTableRelation>>({});
  readonly availableTablesToAdd = input<(resource: string) => string[]>(() => []);
  // Ad-hoc connection details (from the wizard's Step 1 SQL form) — powers the real ALTER TABLE /
  // CREATE TABLE calls below. Only meaningful for destType 'sql'.
  readonly connectionInfo = input<DestinationProbeRequest | null>(null);
  /** False for a host with no decrypted destination credentials to run real DDL with (e.g. the Mapping
   *  Profiles dialog, which only ever has a destinationId + a read-only schema probe) — hides every
   *  "Create a new table…" entry point and the target card's "+ Add column" trigger for a real (probed)
   *  SQL table, so nothing offers an action that would silently no-op against a null connectionInfo.
   *  Defaults true so the Destination Wizard (which always has real connectionInfo) is unaffected. */
  readonly schemaAuthoringEnabled = input(true);
  /** Outcome of the host's live schema read (see SchemaLoadState). Defaults to 'idle' so a host that never
   *  reads a schema at all is unaffected — only 'loading'/'failed'/'unavailable' surface the notice below. */
  readonly schemaLoadState = input<SchemaLoadState>('idle');

  // Incrementing counters from the dialog header's "Load JSON payload"/"Preview output" buttons (moved
  // there so this canvas's own toolbar row can be dropped, giving the viewport back that height) — same
  // pattern as DestinationWizardComponent's exitMappingRequest/saveMappingRequest.
  readonly openLoadPayloadRequest = input<number>(0);
  readonly openPreviewRequest = input<number>(0);
  // null until the effect below has observed a real (post-input-binding) value — these counters live
  // on an ancestor that outlives the canvas and never resets, so a remounted instance (e.g. re-entering
  // a group whose mapping is already done) must learn its baseline from whatever the counter already
  // is, not assume 0, or it mistakes an old, already-handled click for a fresh one and pops the modal
  // with no user action this time. Reading the input in a field initializer is too early — Angular
  // hasn't bound the real value from the parent yet — so the baseline is captured lazily instead.
  private _lastLoadPayloadTrigger: number | null = null;
  private _lastPreviewTrigger: number | null = null;

  // Same relocated-to-the-dialog-header counter pattern as openLoadPayloadRequest/openPreviewRequest
  // above, for the "Suggest mappings"/"Clear suggestions" buttons and the zoom-dock controls — moved up
  // so the canvas's own floating "Suggest mappings" chip and top-right zoom dock can be dropped, giving
  // the viewport back that chrome and letting them live in the dialog's mapping toolbar instead.
  readonly runSuggestMappingsRequest = input<number>(0);
  readonly clearSuggestionsRequest = input<number>(0);
  readonly zoomInRequest = input<number>(0);
  readonly zoomOutRequest = input<number>(0);
  readonly zoomResetRequest = input<number>(0);
  readonly zoomFitRequest = input<number>(0);
  /** Zoom level this canvas instance starts at (1 = 100%) — defaults to the anchor service's own 100%
   *  default, so every consumer except one that explicitly opts in (the New Mapping Profile screen wants
   *  80%) is unaffected. Applied once in the constructor below, not tied to resetView()'s own 100% — the
   *  ⟲ reset button still resets to 100% everywhere, this only changes what the canvas opens at. */
  readonly initialZoom = input<number>(1);

  /** Mirrors suggestions().length / zoomPercent() up to the dialog header, which now renders the
   *  "Clear N suggestions" label and the zoom-percent readout in its own toolbar (see the requests
   *  above for the matching down-direction triggers). */
  readonly suggestionCountChange = output<number>();
  readonly zoomPercentChange = output<string>();

  readonly mappingRowsChange = output<MappingRow[]>();
  readonly targetByResourceChange = output<Record<string, string>>();
  readonly extraTablesChange = output<string[]>();
  /** A column was really added (ALTER TABLE succeeded) — the parent appends it to its own sqlTables().
   *  `table` is only set when the target table didn't already exist and had to be auto-created — the
   *  parent registers it as a brand-new entry rather than trying (and failing) to find an existing one. */
  readonly columnAdded = output<{ tableName: string; column: DestinationColumn; table?: DestinationTable }>();
  /** A table was really created (CREATE TABLE succeeded) — the parent registers it in its own sqlTables(). */
  /** Emits the FULL created table (all columns, including the FK if this was made a child of a parent
   *  table) — the wizard syncs this straight into its sqlTables() signal, no re-probe needed. */
  readonly tableCreated = output<DestinationTable>();
  /** A column was really dropped (DROP COLUMN succeeded) — the parent removes it from its own sqlTables(). */
  readonly columnDropped = output<{ tableName: string; column: string }>();
  /** A column was really altered (ALTER COLUMN + optional sp_rename succeeded) — the parent updates its
   *  own sqlTables() entry, and re-points any mapping that targeted the old column name. */
  readonly columnAltered = output<{ tableName: string; oldColumnName: string; column: DestinationColumn }>();
  /** A JSON payload was successfully parsed — the parent stores these fields as the resource's source tree. */
  readonly sourcePayloadLoaded = output<{ resource: string; fields: ResourceFieldDef[] }>();
  /** "Reset to Original" was clicked in the Load JSON Payload modal — the parent drops this resource's
   *  pasted-payload override (payloadFieldsByResource lives on the parent, not here) so availableFields()
   *  falls back through to the real catalog again, same as defaultAvailableFields already does. */
  readonly sourcePayloadReset = output<string>();
  /** A DESTINATION JSON payload was successfully parsed (submitLoadDestinationPayload) — carries the
   *  ready-to-use Request Body Template built from it (every leaf replaced by its own {{ColumnName}}
   *  placeholder). Only meaningful for the ApiEndpoint destination — the parent (DestinationWizardComponent)
   *  pushes this straight into that destination's own form, so the user never hand-writes placeholders
   *  matching column names themselves. Every other file-shaped destination type has no template concept
   *  and the parent simply ignores this for them. Carries `resource` (which card this came from) so the
   *  parent can route it correctly in a multi-resource ApiEndpoint destination — where EVERY participating
   *  resource type needs its OWN template (see ApiEndpointDestinationFormComponent.setRecordTemplateForResource)
   *  rather than all of them colliding into the one single-resource dest_apiBodyTemplateJson field. */
  readonly destinationTemplateGenerated = output<{ resource: string; templateJson: string }>();
  /** A table created via "Create a new table…" was given a parent/FK relationship — the parent wizard
   *  owns this globally (it outlives any one resource's canvas instance) so it survives navigating
   *  between resources, node reload, and the Mapping JSON export/import. */
  readonly childTableRelationAdded = output<{ tableName: string; relation: ChildTableRelation }>();
  /** A schema-authoring action (create table / add column / drop column / alter column) was triggered —
   *  queued for the wizard to actually execute (via a real DDL call) only once "Add to Pipeline" is
   *  clicked, instead of hitting the live database immediately. See PendingSchemaOp's own doc comment. */
  readonly schemaOpQueued = output<PendingSchemaOp>();
  /** The named table was just removed from the canvas (confirmRemoveTable) — the parent should drop any
   *  of ITS OWN still-queued schema ops (e.g. the "createTable" op that made it exist as a preview in the
   *  first place, or an "addColumn" queued on it before it was removed). Without this, removing a table
   *  the user created and then decided against leaves its queue entries dangling: nothing in the canvas
   *  still shows or maps to the table, but "Add to Workflow" would still create it for real, and any
   *  validation still referencing those stale ops would keep naming a table that's no longer on screen. */
  readonly schemaOpsCancelledForTable = output<string>();
  /** The user asked to re-attempt a schema read that failed or could not be attempted — the host owns the
   *  retry, since it owns the connection details this canvas never sees. */
  readonly retrySchemaLoad = output<void>();

  // ── local UI state ──────────────────────────────────────────────────────
  readonly collapsedIds = signal<Set<string>>(new Set());
  readonly armedId = signal<string | null>(null);
  private armedKind: 'group' | 'leaf' | null = null;
  readonly dragTempWire = signal<FmTempWire | null>(null);
  private dragSourceId: string | null = null;
  private dragSourceKind: 'group' | 'leaf' | null = null;
  /** `focusSourceFhirPath` is set whenever the popover is opened for one specific source rather than the
   *  whole row — a source-field click, a connector-line click (onWireClick — resolved from the actual
   *  wire clicked, see FmWirePath.sourceFhirPath, never guessed), or a "Select mapping" picker choice
   *  (onSourceFieldClick/onTargetFieldClick/onFieldPickerChoice below). Left null/absent only by the
   *  mapping-list's "Edit" button (onEditRow), which keeps opening the whole joined row exactly as
   *  before. It never changes what MappingRow gets looked up (still purely resource/tableName/targetName,
   *  same identity as always) — it only tells popoverDisplayRow which ONE of that row's sources to show,
   *  when the row is a join. Clearing it back to null (without touching resource/tableName/targetName) —
   *  see onShowAllSources — re-reveals every source of that same row. */
  readonly popoverKey = signal<{
    resource: string; tableName: string; targetName: string; focusSourceFhirPath?: string | null;
  } | null>(null);
  /** Non-null while the "Select mapping" popover (a source or target FIELD click that's ambiguous
   *  between more than one option) is open — see openFieldPicker. A field click that resolves to exactly
   *  one option skips this entirely and sets popoverKey directly, same as a connector-line click. */
  readonly fieldPickerState = signal<{
    /** Fully composed second header line, e.g. "6 source mappings → name" or "2 mappings for Name >
     *  Family" — built once at the call site (onSourceFieldClick/onTargetFieldClick), which already knows
     *  which direction of ambiguity this is, rather than the template gluing a title+count together
     *  itself (that used to read as "name · 6 mappings" — the field name first, count second, with no
     *  indication of direction at all). */
    subtitle: string;
    options: FmMappingChoice[];
    style: { top?: number; bottom?: number; left: number; width: number; maxHeight: number };
  } | null>(null);
  readonly drawerOpen = signal(false);
  /** Free-text column names typed via "+ Add column", or loaded in bulk via "Load JSON payload" (CSV /
   *  un-probed SQL only), keyed by "resource::tableName" — INCLUDES columns with no mapping yet, not just
   *  mapped ones (columnsForCardFn unions this with the mapped-column names, so an unmapped column stays
   *  visible on the card for the user to map later instead of silently disappearing). Lifted to the host
   *  (like mappingRows/targetByResource above) rather than owned locally, specifically so
   *  DestinationWizardComponent can persist it (dest_pendingColumns) and restore it on reopen — a purely
   *  local signal here would reset to empty on every fresh canvas instance, which is exactly what used to
   *  make an unmapped column loaded via "Load JSON payload" vanish the moment you left and reopened the
   *  node. A host that doesn't care (e.g. the Mapping Profiles dialog) simply never reads the change output
   *  and lets it default to {}. */
  readonly pendingFreeColumns = input<Record<string, string[]>>({});
  readonly pendingFreeColumnsChange = output<Record<string, string[]>>();

  // Drop-target sentinel for a free-text card's own "+ Add column" row (see
  // field-mapping-target-card's template) — lets a payload field be dropped straight onto an empty
  // card with no columns yet, instead of requiring "type a name, then drag" as two separate steps.
  private static readonly NEW_FREE_COLUMN_DROP_KEY = '__new__';

  // ── freely-draggable card positions ─────────────────────────────────────
  // Keyed by table full-name for target cards (unique per table), plus a reserved key for the source card.
  private static readonly SOURCE_KEY = '__source__';
  private readonly cardPositions = signal<Record<string, { x: number; y: number }>>({});

  // Source tree defaults to 380px wide (field-mapping-source-tree.component.scss) starting at x:24, so its
  // right edge sits at x:404 by default — target cards must start past that with a real gap, not right at
  // it, or the two panels render touching/overlapping the moment neither has been dragged yet.
  private static readonly SOURCE_DEFAULT_WIDTH = 380;
  private static readonly CARD_GAP = 56;

  private defaultPositionFor(key: string, index: number): { x: number; y: number } {
    if (key === FieldMappingCanvasComponent.SOURCE_KEY) return { x: 24, y: 24 };
    const cardX = 24 + FieldMappingCanvasComponent.SOURCE_DEFAULT_WIDTH + FieldMappingCanvasComponent.CARD_GAP;
    return { x: cardX, y: 24 + index * 260 };
  }

  positionForKey(key: string, index: number): { x: number; y: number } {
    return this.cardPositions()[key] ?? this.defaultPositionFor(key, index);
  }

  readonly sourcePosition = computed(() => this.positionForKey(FieldMappingCanvasComponent.SOURCE_KEY, 0));

  onSourcePositionChange(pos: { x: number; y: number }): void {
    this.cardPositions.update(m => ({ ...m, [FieldMappingCanvasComponent.SOURCE_KEY]: pos }));
    this.anchors.refreshAll();
  }

  onCardPositionChange(tableKey: string, pos: { x: number; y: number }): void {
    this.cardPositions.update(m => ({ ...m, [tableKey]: pos }));
    this.anchors.refreshAll();
  }

  // ── target cards: primary table + any extra tables, for the single active resource ──────
  // The primary card is only shown once it points at a real table — a guessed default ("dbo.Encounter")
  // that was never actually created has nothing to map onto, so a card for it would just be a dead-end
  // placeholder. Until then, the canvas-level "+ Add a table…" control is the one way in (see
  // onAddExtraTable/openCreateTableModal, which route there instead of "extra" while this is false).
  private isPrimaryTargetValid(resource: string): boolean {
    const target = this.targetFor(resource);
    // Empty is never valid, for any destType — this is also what makes removing the primary card actually
    // remove it (confirmRemoveTable clears targetByResource[resource] to '') instead of it reappearing
    // immediately with the resource name as a fallback label, which is what happened before this check
    // when hasSqlTables() is false (every non-SQL destination, including Mongo): the OR short-circuited to
    // true regardless of whether target was actually set to anything.
    if (!target) return false;
    return (
      !this.hasSqlTables() ||
      this.sqlTableOptions().includes(target) ||
      // MySQL-only bare-name fallback — see sqlTableNames' doc comment above.
      (this.destType() === 'mysql' && this.sqlTableNames().includes(target))
    );
  }

  readonly targetCards = computed<FmTargetCardSpec[]>(() => {
    const resource = this.resources()[0];
    if (!resource) return [];
    const primary: FmTargetCardSpec[] = this.isPrimaryTargetValid(resource)
      ? [{ resource, tableName: this.targetFor(resource), isExtra: false }]
      : [];
    const extras: FmTargetCardSpec[] = this.extraTables().map(t => ({ resource, tableName: t, isExtra: true }));
    return [...primary, ...extras];
  });

  // ── derived data ─────────────────────────────────────────────────────────
  readonly forest = computed<FmTreeNode[]>(() => buildForest(this.resources(), this.availableFields()));

  private readonly mappedSourceIds = computed<Set<string>>(() => {
    const ids = new Set<string>();
    for (const row of this.mappingRows()) {
      if (row.mode === 'childJson' && row.childNodeId) ids.add(row.childNodeId);
      row.sources.forEach(s => ids.add(s.fhirPath));
    }
    return ids;
  });

  readonly isApproximatedFn = (row: MappingRow) => isApproximated(row);

  // ── near-match auto-suggest ("Suggest mappings" button — never runs on table selection, see
  // field-mapping-automap.util.ts's suggestMappings doc comment) ──────────────────────────────────
  private readonly rawSuggestions = signal<MappingSuggestion[]>([]);
  /** Drops any suggestion whose column has since become really mapped (accepted here, or mapped
   *  manually elsewhere) — a stale suggestion wire pointing at an already-mapped column would be
   *  confusing (it reads as "still needs review" when it's actually done). */
  readonly suggestions = computed<MappingSuggestion[]>(() =>
    this.rawSuggestions().filter(s => !this.rowForColumnFn(s.row.resource, s.row.tableName, s.row.targetName)),
  );

  runSuggestMappings(): void {
    const resource = this.resources()[0];
    if (!resource) return;
    // mappableColumnsForCardFn, not columnsForCardFn directly — result.autoMapped rows get pushed
    // straight into mappingRows below without ever going through completeMapping's own guard, so an
    // identity/FK column has to be excluded from the candidate pool here instead.
    const columnsForResource = (r: string): string[] => this.mappableColumnsForCardFn(r, this.targetFor(r), false);
    const result = suggestMappings(this.forest(), this.mappingRows(), this.targetByResource(), columnsForResource);

    if (result.autoMapped.length) {
      this.mappingRowsChange.emit([...this.mappingRows(), ...result.autoMapped]);
    }
    this.rawSuggestions.set(result.suggestions);

    if (!result.autoMapped.length && !result.suggestions.length) {
      this.toast.info('No new matches', 'No unmapped column matched a source field closely enough to suggest.');
      return;
    }
    const parts: string[] = [];
    if (result.autoMapped.length) parts.push(`${result.autoMapped.length} mapped automatically`);
    if (result.suggestions.length) parts.push(`${result.suggestions.length} awaiting your review`);
    this.toast.success('Suggestions ready', `${parts.join(', ')}.`);
  }

  onSuggestionClick(e: { resource: string; tableName: string; targetName: string }): void {
    const match = this.rawSuggestions().find(
      s => s.row.resource === e.resource && s.row.tableName === e.tableName && s.row.targetName === e.targetName,
    );
    if (!match) return;
    this.mappingRowsChange.emit([...this.mappingRows(), match.row]);
    this.rawSuggestions.update(list => list.filter(s => s !== match));
    this.toast.success('Suggestion accepted', `${match.row.sources[0]?.label ?? ''} → ${e.targetName}`);
  }

  clearSuggestions(): void {
    this.rawSuggestions.set([]);
  }

  /** All tables (primary + extras) a resource currently targets — used by the mapping-list draft form. */
  tablesForResourceFn = (resource: string): string[] =>
    this.targetCards().filter(c => c.resource === resource).map(c => c.tableName);

  /** Column list for (resource, tableName) — used by the mapping-list draft form. Excludes whatever
   *  isProtectedColumn does (see mappableColumnsForCardFn) — this is a column PICKER, not the target
   *  card's own display, so it should never offer one a mapping could never actually target. */
  columnsForResourceTableFn = (resource: string, tableName: string): string[] => {
    const card = this.targetCards().find(c => c.resource === resource && c.tableName === tableName);
    return this.mappableColumnsForCardFn(resource, tableName, card?.isExtra ?? false);
  };

  constructor() {
    // React only on an actual increment while this instance is alive. The first run just learns the
    // real baseline (whatever the ancestor's counter already sits at) rather than acting on it — that
    // first value is never "the user just clicked", it's this instance catching up.
    effect(() => {
      const v = this.openLoadPayloadRequest();
      if (this._lastLoadPayloadTrigger === null) { this._lastLoadPayloadTrigger = v; return; }
      if (v !== this._lastLoadPayloadTrigger) { this._lastLoadPayloadTrigger = v; if (v > 0) this.openLoadPayloadModal(); }
    });
    effect(() => {
      const v = this.openPreviewRequest();
      if (this._lastPreviewTrigger === null) { this._lastPreviewTrigger = v; return; }
      if (v !== this._lastPreviewTrigger) { this._lastPreviewTrigger = v; if (v > 0) this.openDrawer(); }
    });
    this.watchTrigger(this.runSuggestMappingsRequest, () => this.runSuggestMappings());
    this.watchTrigger(this.clearSuggestionsRequest, () => this.clearSuggestions());
    this.watchTrigger(this.zoomInRequest, () => this.zoomIn());
    this.watchTrigger(this.zoomOutRequest, () => this.zoomOut());
    this.watchTrigger(this.zoomResetRequest, () => this.resetView());
    this.watchTrigger(this.zoomFitRequest, () => this.fitToView());

    effect(() => this.suggestionCountChange.emit(this.suggestions().length));
    effect(() => this.zoomPercentChange.emit(this.zoomPercent()));

    // Closes the "Select mapping" picker if the canvas pans or zooms while it's open — its position was
    // computed once, in real screen pixels, from the clicked field's own row at open time
    // (computeFieldPickerStyle); panning/zooming moves that row without moving the panel (position:
    // fixed), so leaving it open would leave it floating over the wrong spot on screen. Same
    // baseline-learning shape as watchTrigger above (an effect runs immediately on creation, and that
    // first run is this instance catching up to whatever pan/zoom already is, never a real user action).
    let lastPan: { x: number; y: number } | null = null;
    let lastZoom: number | null = null;
    effect(() => {
      const pan = this.anchors.pan();
      const zoom = this.anchors.zoom();
      const changed = lastPan !== null && (pan.x !== lastPan.x || pan.y !== lastPan.y || zoom !== lastZoom);
      lastPan = pan;
      lastZoom = zoom;
      if (changed && this.fieldPickerState()) this.closeFieldPicker();
    });
  }

  /** Same baseline-learning behavior as the openLoadPayloadRequest/openPreviewRequest effects above,
   *  factored out for the newer relocated triggers (suggest/clear/zoom) so each doesn't need its own
   *  hand-written baseline field. */
  private watchTrigger(source: () => number, onFire: () => void): void {
    let last: number | null = null;
    effect(() => {
      const v = source();
      if (last === null) { last = v; return; }
      if (v !== last) { last = v; if (v > 0) onFire(); }
    });
  }

  ngOnInit(): void {
    // NOT in the constructor — signal inputs only reflect their bound value (vs. their declared default)
    // once Angular has actually applied bindings, which happens after construction but before ngOnInit.
    // Reading initialZoom() in the constructor silently returned the default (1) every time, regardless
    // of what the host template bound it to.
    this.anchors.setZoom(this.initialZoom());
  }

  ngAfterViewInit(): void {
    const origin = this.canvasInner().nativeElement;
    this.anchors.setOrigin(origin);
    this.resizeObserver = new ResizeObserver(() => this.anchors.refreshAll());
    this.resizeObserver.observe(origin);

    const viewportEl = this.viewport().nativeElement;
    this.anchors.setViewportSize(viewportEl.clientWidth, viewportEl.clientHeight);
    this.viewportResizeObserver = new ResizeObserver(() =>
      this.anchors.setViewportSize(viewportEl.clientWidth, viewportEl.clientHeight));
    this.viewportResizeObserver.observe(viewportEl);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.viewportResizeObserver?.disconnect();
  }

  // ── viewport pan / zoom (mirrors the main workflow canvas's CanvasServiceV2 interaction pattern:
  // click-drag empty space to pan, wheel to zoom — see canvas.component.ts's onShellPointer*/onWheel) ──
  readonly transformStyle = this.anchors.transformStyle;
  readonly zoomPercent = this.anchors.zoomPercent;
  readonly panning = signal(false);

  private panStart: { pointerId: number; startX: number; startY: number; panX: number; panY: number } | null = null;

  /** Empty-space click only — true when the pointerdown landed directly on the viewport or canvas-inner
   *  background, never on a card/tree-node/control (those capture the pointer themselves on mousedown). */
  onViewportPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    if (ev.target !== this.viewport().nativeElement && ev.target !== this.canvasInner().nativeElement) return;

    const pan = this.anchors.pan();
    this.panStart = { pointerId: ev.pointerId, startX: ev.clientX, startY: ev.clientY, panX: pan.x, panY: pan.y };
    this.panning.set(true);
    (ev.currentTarget as HTMLElement).setPointerCapture(ev.pointerId);
  }

  onViewportPointerMove(ev: PointerEvent): void {
    if (!this.panStart || ev.pointerId !== this.panStart.pointerId) return;
    this.anchors.setPan(
      this.panStart.panX + (ev.clientX - this.panStart.startX),
      this.anchors.clampPanY(this.panStart.panY + (ev.clientY - this.panStart.startY)),
    );
  }

  onViewportPointerUp(ev: PointerEvent): void {
    if (!this.panStart || ev.pointerId !== this.panStart.pointerId) return;
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.panStart = null;
    this.panning.set(false);
  }

  /** Plain wheel/trackpad scrolls the canvas vertically (bounded, like a real scrollbar); Ctrl/Cmd+wheel
   *  zooms, anchored at the cursor — matches the vertical scrollbar's own bounded range exactly, since
   *  both go through clampPanY/setScrollY. Zoom still works no matter what's under the cursor.
   *
   *  Plain wheel used to defer to the browser's native scroll instead whenever hovering a card's own
   *  field/column list (.fm-source-rows/.fm-target-rows), but was removed because neither ever actually
   *  grew a real scrollbar of its own (both cards were deliberately auto-height/uncapped) — that exemption
   *  just silently ate the wheel event over a long list with nothing picking it up. Now that both cards'
   *  "fit to screen" toggle (see FieldMappingSourceTreeComponent/FieldMappingTargetCardComponent's
   *  fitMode) can cap their height and make that row region genuinely scrollable, the exemption is
   *  restored — but conditioned on the row region actually having overflow right now (scrollHeight >
   *  clientHeight), so it only ever activates when there's really something to scroll. Without that
   *  condition, hovering the same region in "expand" mode (or a list short enough to fit) would
   *  reintroduce the exact dead-zone bug this history describes. Still unconditionally deferred for
   *  .fm-add-table-options, a real, always-genuinely-scrollable dropdown panel. */
  onViewportWheel(ev: WheelEvent): void {
    // The join/mapping-config popover and the "Select mapping" picker are both fixed overlay siblings
    // floating on top of the canvas (see the template's own "never descendants of .fm-canvas-inner"
    // comment), not part of the pannable/zoomable surface itself — checked BEFORE the ctrl/cmd zoom
    // branch below, so neither scrolling NOR zooming (ctrl/cmd+wheel) over either one ever reaches the
    // canvas; both are still DOM descendants of .fm-viewport (fixed positioning doesn't change where
    // wheel events bubble to), so without this check scrolling/zooming over them fell into the same
    // default branch as empty canvas space and panned/zoomed the whole canvas underneath them instead.
    if ((ev.target as HTMLElement).closest('.fm-popover, .fm-field-picker')) {
      return;
    }
    if (ev.ctrlKey || ev.metaKey) {
      ev.preventDefault();
      this.anchors.setZoom(this.anchors.zoom() * (ev.deltaY < 0 ? 1.1 : 0.9), ev.clientX, ev.clientY);
      return;
    }
    if ((ev.target as HTMLElement).closest('.fm-add-table-options')) {
      return;
    }
    const rows = (ev.target as HTMLElement).closest<HTMLElement>('.fm-source-rows, .fm-target-rows');
    if (rows && rows.scrollHeight > rows.clientHeight) {
      return;
    }
    ev.preventDefault();
    this.anchors.setScrollY(this.anchors.scrollY() + ev.deltaY);
  }

  // ── vertical scrollbar (custom-built, not a native overflow scrollbar — the canvas's virtual content
  // is transform-scaled, which native overflow can't reliably size against across browsers; this reads
  // the exact same pan/zoom state instead) ──
  readonly scrollThumbFraction = this.anchors.scrollThumbFraction;
  readonly scrollY = this.anchors.scrollY;
  readonly maxScrollY = this.anchors.maxScrollY;
  /** Thumb's top offset as a fraction of the track — 0 at the top, (1 - thumbFraction) at the bottom. */
  readonly scrollThumbTopFraction = computed(() => {
    const max = this.maxScrollY();
    return max > 0 ? (this.scrollY() / max) * (1 - this.scrollThumbFraction()) : 0;
  });

  private thumbDragStart: { pointerId: number; startClientY: number; startScrollY: number; trackHeight: number } | null = null;

  onScrollThumbPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    ev.stopPropagation(); // don't let this also start a viewport drag-pan
    const track = (ev.currentTarget as HTMLElement).parentElement;
    this.thumbDragStart = {
      pointerId: ev.pointerId,
      startClientY: ev.clientY,
      startScrollY: this.anchors.scrollY(),
      trackHeight: track?.clientHeight ?? this.anchors.viewportSize().height,
    };
    (ev.currentTarget as HTMLElement).setPointerCapture(ev.pointerId);
  }

  onScrollThumbPointerMove(ev: PointerEvent): void {
    if (!this.thumbDragStart || ev.pointerId !== this.thumbDragStart.pointerId) return;
    const travel = this.thumbDragStart.trackHeight * (1 - this.anchors.scrollThumbFraction());
    if (travel <= 0) return;
    const deltaScroll = ((ev.clientY - this.thumbDragStart.startClientY) / travel) * this.anchors.maxScrollY();
    this.anchors.setScrollY(this.thumbDragStart.startScrollY + deltaScroll);
  }

  onScrollThumbPointerUp(ev: PointerEvent): void {
    if (!this.thumbDragStart || ev.pointerId !== this.thumbDragStart.pointerId) return;
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.thumbDragStart = null;
  }

  /** Clicking empty track (not the thumb itself) pages toward the click, like a native scrollbar. */
  onScrollTrackClick(ev: MouseEvent): void {
    if (ev.target !== ev.currentTarget) return;
    const rect = (ev.currentTarget as HTMLElement).getBoundingClientRect();
    const clickScrollPos = ((ev.clientY - rect.top) / rect.height) * this.anchors.maxScrollY();
    const current = this.anchors.scrollY();
    const page = this.anchors.viewportSize().height * 0.9;
    this.anchors.setScrollY(clickScrollPos > current ? current + page : current - page);
  }

  onScrollTrackKeydown(ev: KeyboardEvent): void {
    const current = this.anchors.scrollY();
    const page = this.anchors.viewportSize().height * 0.9;
    switch (ev.key) {
      case 'ArrowUp':   this.anchors.setScrollY(current - 40); break;
      case 'ArrowDown': this.anchors.setScrollY(current + 40); break;
      case 'PageUp':    this.anchors.setScrollY(current - page); break;
      case 'PageDown':  this.anchors.setScrollY(current + page); break;
      case 'Home':      this.anchors.setScrollY(0); break;
      case 'End':       this.anchors.setScrollY(this.anchors.maxScrollY()); break;
      default: return;
    }
    ev.preventDefault();
  }

  zoomIn(): void { this.anchors.setZoom(this.anchors.zoom() + 0.1); }
  zoomOut(): void { this.anchors.setZoom(this.anchors.zoom() - 0.1); }
  resetView(): void { this.anchors.resetView(); }
  fitToView(): void {
    const rect = this.viewport().nativeElement.getBoundingClientRect();
    this.anchors.fitToView(rect.width, rect.height);
  }

  // ── tree fold state ──────────────────────────────────────────────────────
  isCollapsedFn = (id: string): boolean => this.collapsedIds().has(id);
  onToggleCollapse(id: string): void {
    this.collapsedIds.update(set => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  isMappedFn = (id: string): boolean => this.mappedSourceIds().has(id);
  isArmedFn = (id: string): boolean => this.armedId() === id;

  // ── row lookup ───────────────────────────────────────────────────────────
  rowForColumnFn = (resource: string, tableName: string, column: string): MappingRow | undefined =>
    this.mappingRows().find(r => r.resource === resource && r.tableName === tableName && r.targetName === column);

  rowForColumnOn(resource: string, tableName: string) {
    return (column: string) => this.rowForColumnFn(resource, tableName, column);
  }

  columnTypeOn(tableName: string) {
    return (column: string) => this.dataTypeForTable()(tableName, column);
  }

  columnKeyInfoOn(tableName: string) {
    return (column: string) => this.keyInfoForTable()(tableName, column);
  }

  /** Mirrors serializeRowsFlat's precedence: once ANY row for the resource has an explicit isUpsertKey,
   *  that fully decides the effective key for every row of the resource — the real PK badge is only
   *  consulted as a fallback when nothing has been explicitly designated yet. */
  isUpsertKeyColumnOn(resource: string, tableName: string) {
    return (column: string): boolean => {
      const row = this.rowForColumnFn(resource, tableName, column);
      if (!row) return false;
      const hasExplicitKey = this.mappingRows().some(r => r.resource === resource && r.isUpsertKey === true);
      return hasExplicitKey
        ? row.isUpsertKey === true
        : !!this.keyInfoForTable()(tableName, column)?.isPrimaryKey;
    };
  }

  relationFor(tableName: string): ChildTableRelation | undefined {
    return this.childTableRelations()[tableName];
  }

  targetFor(resource: string): string { return this.targetByResource()[resource] ?? ''; }

  onTargetChange(resource: string, value: string): void {
    this.targetByResourceChange.emit({ ...this.targetByResource(), [resource]: value });
  }

  /**
   * Live column list for one target card: real schema when probed, else mapped + pending free-text
   * names — the free-text fallback applies to extra tables exactly like the primary table, since
   * without a live connection there's no real schema to distinguish "existing" from "new" by.
   */
  columnsForCardFn(resource: string, tableName: string, isExtra: boolean): string[] {
    if (isExtra && this.hasSqlTables()) return this.columnsForTable()(tableName);
    if (!isExtra && this.hasSqlTables()) return this.columnsForResourceTarget()(resource);
    const key = `${resource}::${tableName}`;
    const mapped = this.mappingRows()
      .filter(r => r.resource === resource && r.tableName === tableName)
      .map(r => r.targetName);
    const pending = this.pendingFreeColumns()[key] ?? [];
    return Array.from(new Set([...mapped, ...pending]));
  }

  onAddFreeColumn(resource: string, tableName: string, column: string): void {
    if (this.columnsForCardFn(resource, tableName, false).includes(column)) {
      this.toast.warning('Column exists', `${column} is already on ${tableName || resource}.`);
      return;
    }
    this.registerPendingColumn(resource, tableName, column);
    this.toast.info('Column added', `${column} is ready to map — drag a field onto it.`);
  }

  /**
   * Tracks a column locally so it shows up on the card immediately — the only way to reflect it while
   * no live schema is known (hasSqlTables() false), since columnsForCardFn's real-schema branch isn't
   * reachable yet. Harmless no-op for display once hasSqlTables() is true (that branch takes over).
   */
  private registerPendingColumn(resource: string, tableName: string, column: string): void {
    const key = `${resource}::${tableName}`;
    this.updatePendingFreeColumns(m => ({ ...m, [key]: [...(m[key] ?? []), column] }));
  }

  /** pendingFreeColumns is host-owned (see its own doc comment) — every mutation here reads the current
   *  input value, applies `updater`, and emits the result rather than calling a local .update(), so the
   *  host's signal (and whatever persists it) always reflects the latest state. */
  private updatePendingFreeColumns(
    updater: (m: Record<string, string[]>) => Record<string, string[]>,
  ): void {
    this.pendingFreeColumnsChange.emit(updater(this.pendingFreeColumns()));
  }

  /** Renames a free-text column (CSV, or SQL before a live schema is known) — no real ALTER COLUMN
   *  involved, just this resource's own local column list + any mapping already pointed at the old
   *  name. Unlike a real schema column (see openEditColumnModal/submitEditColumn, which queues a real
   *  ALTER COLUMN + sp_rename), there's nothing to flush on "Add to Pipeline". */
  onRenameFreeColumn(resource: string, tableName: string, oldName: string, newName: string): void {
    const trimmed = newName.trim();
    if (!trimmed || trimmed === oldName) return;
    if (this.columnsForCardFn(resource, tableName, false).includes(trimmed)) {
      this.toast.warning('Column exists', `${trimmed} is already on ${tableName || resource}.`);
      return;
    }
    const key = `${resource}::${tableName}`;
    this.updatePendingFreeColumns(m => ({
      ...m,
      [key]: (m[key] ?? []).map(c => (c === oldName ? trimmed : c)),
    }));
    this.mappingRowsChange.emit(this.mappingRows().map(r =>
      r.resource === resource && r.tableName === tableName && r.targetName === oldName
        ? { ...r, targetName: trimmed }
        : r
    ));
    this.toast.success('Column renamed', `${oldName} → ${trimmed}.`);
  }

  // ── extra tables ("+ Add a table from your database…") ─────────────────
  // Whether the "Create a new table…" modal is currently open — toggled on by picking that option from
  // the dropdown, and back off once a create attempt resolves (or is cancelled).
  readonly creatingNewTable = signal(false);
  readonly createNewTableOption = '__create_new_table__';
  readonly creatingTableSubmitting = signal(false);
  readonly creatingTableError = signal<string | null>(null);
  private creatingTableResource: string | null = null;
  private creatingTableAsPrimary = false;

  /** A native <select>'s open option list is rendered by the OS/browser itself — no page CSS/DOM can
   *  size, position, or inject a search box into it, so it can't be kept inside the canvas's own
   *  visible bounds as that shrinks, nor filtered as the user types. This custom panel renders
   *  position: fixed, sized/positioned from real getBoundingClientRect() measurements of the trigger
   *  button and the canvas viewport itself (see toggleAddTableMenu) — the same escape-the-clipping-
   *  ancestor technique workflow-list.component.ts's .row-menu-panel already uses for an analogous
   *  overflow problem. */
  readonly addTableMenuOpen = signal(false);
  readonly addTableMenuStyle = signal<{ top?: number; bottom?: number; left: number; width: number; maxHeight: number } | null>(null);
  readonly addTableSearchQuery = signal('');

  /** Opens/closes the custom "+ Add a table…" panel, sized to whichever of (space below the trigger,
   *  space above it) is larger within the canvas's own viewport — not the browser window — so it never
   *  grows past what's actually visible even when the canvas panel itself is small. */
  toggleAddTableMenu(event: MouseEvent): void {
    if (this.addTableMenuOpen()) {
      this.closeAddTableMenu();
      return;
    }

    const triggerEl = event.currentTarget as HTMLElement;
    const triggerRect = triggerEl.getBoundingClientRect();
    const viewportRect = this.viewport().nativeElement.getBoundingClientRect();
    const margin = 8;
    const spaceBelow = viewportRect.bottom - triggerRect.bottom - margin;
    const spaceAbove = triggerRect.top - viewportRect.top - margin;
    const minUsableHeight = 120;

    // .fm-add-table-panel renders position: fixed, but this dialog's own chrome (some ancestor between
    // here and <body> — confirmed via a real fixed-position probe's offsetParent, since Chrome only sets
    // that to a non-null element when one exists) establishes its own containing block for fixed
    // descendants here, despite every standard CSS property that's supposed to cause this
    // (transform/filter/backdrop-filter/will-change/contain/perspective) reporting none/default the whole
    // way up every ancestor. Rather than depend on knowing exactly which ancestor or why, a throwaway
    // probe dropped into this trigger's own parent (the same place the panel itself renders from) reports
    // the real containing block directly, so left/top/bottom below are expressed relative to THAT — not
    // naively assumed to be raw viewport coordinates, which is what silently rendered this panel well
    // outside the visible canvas (confirmed up to ~220px right of every calculation's own intent).
    const probe = document.createElement('div');
    probe.style.cssText = 'position:fixed; left:0; top:0; width:0; height:0; visibility:hidden;';
    triggerEl.parentElement!.appendChild(probe);
    const containingBlock = probe.offsetParent
      ? probe.offsetParent.getBoundingClientRect()
      : new DOMRect(0, 0, window.innerWidth, window.innerHeight);
    probe.remove();

    // Clamp horizontally within whichever is narrower — the canvas's own visible area (.fm-viewport) or
    // the actual browser window — same margin as the vertical clamp above. The trigger can sit anywhere
    // along the canvas's pannable/zoomable width, including right up against its right edge (e.g. panned/
    // zoomed so "+ Add a table…" ends up near the dialog's edge), and this panel is otherwise sized to
    // exactly the trigger's own width with nothing keeping its right edge from running past the canvas.
    // Bounding against the window too (not just .fm-viewport) covers a narrower browser/lower zoom level,
    // where the dialog itself can be wider than what's actually visible on screen. These are all still
    // real viewport coordinates — only the final style values (below) get translated into the panel's
    // actual containing block.
    const panelWidth = triggerRect.width;
    const rightBound = Math.min(viewportRect.right, window.innerWidth) - margin;
    const desiredLeft = Math.min(
      Math.max(viewportRect.left + margin, triggerRect.left),
      Math.max(viewportRect.left + margin, rightBound - panelWidth),
    );
    const left = desiredLeft - containingBlock.left;

    this.addTableMenuStyle.set(
      spaceBelow >= minUsableHeight || spaceBelow >= spaceAbove
        ? { top: triggerRect.bottom + 6 - containingBlock.top, left, width: panelWidth, maxHeight: Math.max(minUsableHeight, spaceBelow) }
        : { bottom: containingBlock.bottom - triggerRect.top + 6, left, width: panelWidth, maxHeight: Math.max(minUsableHeight, spaceAbove) }
    );
    this.addTableSearchQuery.set('');
    this.addTableMenuOpen.set(true);
    // One tick so the panel (and its search input, an @if-conditional sibling of this trigger) has
    // actually rendered before we try to focus it. preventScroll matters here: .fm-viewport has
    // overflow: hidden, and the search input's DOM layout position sits wherever this trigger happens to
    // be on the pannable/zoomable canvas — even though the panel itself renders position: fixed at the
    // coordinates computed above, a plain focus() still makes the browser scroll .fm-viewport's own
    // overflow (based on that unrelated layout position, not the fixed visual one) to "reveal" it,
    // panning the whole canvas out from under the just-positioned panel and throwing off left/top here.
    setTimeout(() => this.addTableSearchInput()?.nativeElement.focus({ preventScroll: true }));
  }

  closeAddTableMenu(): void {
    this.addTableMenuOpen.set(false);
    this.addTableMenuStyle.set(null);
    this.addTableSearchQuery.set('');
  }

  onAddTableSearchInput(value: string): void {
    this.addTableSearchQuery.set(value);
  }

  clearAddTableSearch(): void {
    this.addTableSearchQuery.set('');
    // preventScroll — see toggleAddTableMenu's own comment on its identical focus() call.
    this.addTableSearchInput()?.nativeElement.focus({ preventScroll: true });
  }

  /** Whether the "+ Add a table" slot should render the searchable-list UI (vs. SQL's plain "+ Create a
   *  new table…" @else branch, or nothing at all). Deliberately separate from the hasSqlTables INPUT —
   *  that one also drives isPrimaryTargetValid's "primary target must be a known table" gate below, which
   *  must stay false for Mongo (a not-yet-created collection is a valid primary target — the writer creates
   *  a missing one on its first write). Mongo always gets the searchable UI regardless
   *  of hasSqlTables()/whether any collections were loaded yet, since its own "type a new collection name"
   *  option (see the template) needs the search box open even against a brand-new, empty database. */
  showAddTablePicker(): boolean {
    return this.hasSqlTables() || this.destType() === 'mongo';
  }

  /** Whether to say, in place of the (absent) table list, that the list is still loading or could not be
   *  loaded at all. Only reachable when the picker itself can't render — with a real list on screen there
   *  is nothing to explain. Guards against the failure this exists to fix: a schema read that never landed
   *  is otherwise indistinguishable from a database with no tables in it. */
  showSchemaLoadNotice(): boolean {
    if (this.showAddTablePicker()) return false;
    const state = this.schemaLoadState();
    return state === 'loading' || state === 'failed' || state === 'unavailable';
  }

  /** availableTablesToAdd() is a plain function input, not itself a signal, so this can't be a
   *  computed() — it just re-filters on every call, same as tablesForResourceFn/columnsForResourceTableFn
   *  above; the table lists involved are small enough that this is cheap per change-detection pass. */
  filteredTablesToAdd(resource: string): string[] {
    const query = this.addTableSearchQuery().trim().toLowerCase();
    const all = this.availableTablesToAdd()(resource);
    return query ? all.filter(t => t.toLowerCase().includes(query)) : all;
  }

  /** Routes a panel selection: the special "create new" sentinel opens the create-table modal;
   *  anything else names an already-probed, already-existing table — no backend call needed. */
  onAddTableSelectChange(resource: string, value: string): void {
    this.closeAddTableMenu();
    if (value === this.createNewTableOption) {
      this.openCreateTableModal(resource);
      return;
    }
    this.onAddExtraTable(resource, value);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClickForAddTableMenu(event: MouseEvent): void {
    if (this.addTableMenuOpen() && !(event.target as HTMLElement).closest('.fm-add-table-slot')) {
      this.closeAddTableMenu();
    }
  }

  /** Set true for exactly one document:click cycle right after opening the "Select mapping" popover
   *  (see activateFieldMappings) — the very field click that opens it also bubbles up to this same
   *  document listener a moment later (pointerup -> fieldClick/columnClick fires synchronously first,
   *  then the browser's own native "click" event follows and bubbles all the way to document), which
   *  would otherwise read as "clicked outside" and immediately close the popover it just opened.
   *  Deliberately NOT a plain stopPropagation() on that originating click instead — this canvas has
   *  other things that legitimately react to "the user clicked anywhere" (e.g. the "+ Add a table…"
   *  menu's own click-outside-closes-it listener just below), which stopping propagation there would
   *  have silently broken for every source/target field click, not just ones that open this popover. */
  private suppressNextFieldPickerOutsideClick = false;

  @HostListener('document:click', ['$event'])
  onDocumentClickForFieldPicker(event: MouseEvent): void {
    if (this.suppressNextFieldPickerOutsideClick) {
      this.suppressNextFieldPickerOutsideClick = false;
      return;
    }
    if (this.fieldPickerState() && !(event.target as HTMLElement).closest('.fm-field-picker')) {
      this.closeFieldPicker();
    }
  }

  @HostListener('document:keydown.escape')
  onEscapeForFieldPicker(): void {
    if (this.fieldPickerState()) this.closeFieldPicker();
  }

  /** The only "create a table" entry point — the target card itself has no table picker of its own
   *  (see field-mapping-target-card.component.html), so this is reached exclusively from the canvas-level
   *  "+ Add a table…" control. asPrimary auto-detects "whatever this resource actually needs right now":
   *  becomes this resource's primary table if it doesn't have a valid one yet, otherwise an extra table. */
  openCreateTableModal(resource: string, asPrimary?: boolean): void {
    if (!this.schemaAuthoringEnabled()) return; // defensive — the triggering UI is hidden below when disabled
    this.creatingTableResource = resource;
    this.creatingTableAsPrimary = asPrimary ?? !this.isPrimaryTargetValid(resource);
    this.creatingTableError.set(null);
    this.creatingNewTable.set(true);
  }

  onAddExtraTable(resource: string, tableName: string): void {
    const name = tableName.trim();
    if (!name) return;
    if (!this.isPrimaryTargetValid(resource)) {
      this.onTargetChange(resource, name);
      this.toast.success('Table set', `${name} is ready to map.`);
      return;
    }
    if (this.extraTables().includes(name) || this.targetFor(resource) === name) {
      this.toast.warning('Table already added', `${name} is already on this canvas.`);
      return;
    }
    this.extraTablesChange.emit([...this.extraTables(), name]);

    // Unlike SQL's child tables, a Mongo collection added here needs no parent-link relation to be written —
    // it's independent, keyed on its own mapped upsert key if any (see ConfiguredPipelineService.
    // BuildChildTableRecords / MappedMongoDestinationWriter.WriteChildTableAsync, which no longer require
    // ForeignKeyColumn metadata to exist at all). No childTableRelationAdded emission needed here.
    this.toast.success('Table added', `${name} is ready to map.`);
  }

  closeCreateTableModal(): void {
    if (this.creatingTableSubmitting()) return;
    this.creatingNewTable.set(false);
    this.creatingTableError.set(null);
    this.creatingTableResource = null;
  }

  /**
   * Submits the create-table modal. Unlike every other schema-authoring action on this canvas, this one
   * DOES make a real (read-only) call before returning — a live existence probe, so a table that already
   * exists in the real database is rejected here, immediately, rather than only once "Add to Pipeline"
   * flushes a doomed CREATE TABLE minutes later (see describeLiveCreateTableConflict). Nothing is queued
   * or previewed until that resolves clean — see _finishCreateTable for the actual staging step, which
   * still queues rather than executes the real DDL, same as every other action here.
   */
  submitCreateTable(submission: FmCreateTableSubmit): void {
    const resource = this.creatingTableResource;
    const typed = submission.tableName.trim();
    if (!resource || !typed) return;
    // Mirrors SqlDestinationSchemaService.SplitTableName's own per-dialect default (via the shared
    // qualifyTableName, field-mapping-model.ts) so extraTables()/sqlTables() agree on the same key
    // everywhere, or the new table's card resolves zero columns via columnsForTable() even though the
    // create appears to have "succeeded" (columns silently invisible).
    const name = qualifyTableName(typed, this.destType());
    const connection = this.connectionInfo();
    if (!connection) return;

    // Fast, synchronous, local-only check first — a name already staged this session (or already known
    // real from the last probe) doesn't need a live round trip to answer. See
    // field-mapping-schema-ops.util.ts's describeCreateTableConflict — blocks only a genuine duplicate,
    // never a resource's own still-unfulfilled guessed target that merely happens to share this name
    // (targetFor(resource) may just be a guess with no card shown for it yet — see isPrimaryTargetValid).
    const localConflict = describeCreateTableConflict(name, {
      extraTables: this.extraTables(),
      sqlTableOptions: this.sqlTableOptions(),
      pendingTableNames: this.pendingTableNames(),
    });
    if (localConflict) {
      this.creatingTableError.set(localConflict);
      return;
    }

    // Nothing locally known either way — that's exactly the gap a possibly-stale/never-probed
    // sqlTableOptions can leave open (see the "TestPatient" bug report this fixes: a table that already
    // exists in the real database but this canvas never learned about it). Confirm against the LIVE
    // database via the same schema-preview probe the Step 1 connection-test flow already uses, before
    // queueing anything — not a second/duplicate existence API, just the existing one called from one
    // more place. No optimistic preview and nothing is queued until this resolves.
    this.creatingTableSubmitting.set(true);
    this.schemaSvc.probe(connection).subscribe({
      next: (probe) => {
        this.creatingTableSubmitting.set(false);
        const liveConflict = describeLiveCreateTableConflict(name, probe);
        if (liveConflict) {
          this.creatingTableError.set(liveConflict);
          return;
        }
        this._finishCreateTable(resource, name, submission, connection);
      },
      error: () => {
        this.creatingTableSubmitting.set(false);
        this.creatingTableError.set(`Could not verify "${name}" against the destination database — try again.`);
      },
    });
  }

  /** Everything that actually stages the create — the local preview + the queued schemaOpQueued('createTable')
   *  op — split out from submitCreateTable so it only ever runs after both the local AND live existence
   *  checks above have cleared. No database call happens here: the real CREATE TABLE only runs once "Add
   *  to Pipeline" flushes the queue (see schemaOpQueued/DestinationWizardComponent), so the table shown
   *  here is a locally-synthesized preview (mirroring SqlDestinationSchemaService.CreateTableAsync's own
   *  shape/defaults) rather than the backend's authoritative response — the flush overwrites it with the
   *  real one once it actually executes. */
  private _finishCreateTable(
    resource: string, name: string, submission: FmCreateTableSubmit, connection: DestinationProbeRequest,
  ): void {
    const columns: DestinationColumn[] = submission.columns.map(c => ({
      name: c.name,
      dataType: c.dataType,
      mappingValueType: this.mapSqlServerType(c.dataType),
      isNullable: true,
      maxLength: null,
      origin: 'userCreated',
    }));

    let relation: ChildTableRelation | undefined;
    if (submission.parentTable) {
      // Mirrors CreateTableRequest's server-side defaults: parentColumn defaults to "Id",
      // foreignKeyColumnName to "{parentTableName}Id" — the deferred flush's real response will overwrite
      // this preview with whatever the backend actually used, in case these ever drift apart.
      const parentShortName = submission.parentTable.includes('.')
        ? submission.parentTable.split('.').pop()!
        : submission.parentTable;
      relation = {
        parentTable: submission.parentTable,
        parentColumn: submission.parentColumn || 'Id',
        foreignKeyColumnName: submission.foreignKeyColumnName || `${parentShortName}Id`,
      };
      columns.push({
        name: relation.foreignKeyColumnName,
        dataType: 'bigint',
        mappingValueType: 'Integer',
        isNullable: false,
        maxLength: null,
        isForeignKey: true,
        references: `${relation.parentTable}.${relation.parentColumn}`,
        origin: 'userCreated',
      });
    }

    // Local preview only — the real backend response (once "Add to Pipeline" flushes this) overwrites
    // it. This used to hardcode 'dbo' as the no-dot fallback, which was harmless for SQL Server/
    // PostgreSQL (name always has a dot by the time it gets here — see qualifyTableName above) but wrong
    // for MySQL, whose name never does: it should be "" (no schema layer), not "dbo".
    const { schemaName, tableName } = splitTableName(name, this.destType());
    const table: DestinationTable = {
      schemaName,
      tableName,
      fullName: name,
      origin: 'userCreated',
      columns: [
        // Mirrors what the real probed PK reports, which is NOT the same for every engine — see
        // SqlDestinationSchemaService.CreateTableAsync, whose result this preview stands in for until the
        // next probe. SQL Server/MySQL/PostgreSQL give Id an IDENTITY/AUTO_INCREMENT default, so the
        // database fills it and the mapping picker (isProtectedColumn) must exclude it. Fabric Warehouse
        // supports no such default, so its Id is an ordinary writable column that has to be mapped —
        // claiming otherwise made this canvas treat it as absent and queue an ADD COLUMN for a column the
        // CREATE TABLE had just created, which the Warehouse rejected as a duplicate.
        {
          name: 'Id', dataType: 'bigint', mappingValueType: 'Integer', isNullable: false, maxLength: null,
          isPrimaryKey: true,
          isAutoGenerated: this.destType() !== 'fabricwarehouse',
          origin: 'userCreated',
        },
        ...columns,
      ],
    };

    this.tableCreated.emit(table);
    if (this.creatingTableAsPrimary) {
      this.onTargetChange(resource, name);
    } else {
      this.extraTablesChange.emit([...this.extraTables(), name]);
    }
    if (relation) {
      this.childTableRelationAdded.emit({ tableName: name, relation });
    }
    this.schemaOpQueued.emit({
      kind: 'createTable',
      request: {
        connection,
        tableName: name,
        columns: submission.columns,
        parentTable: submission.parentTable,
        parentColumn: submission.parentColumn,
        foreignKeyColumnName: submission.foreignKeyColumnName,
      },
    });
    this.toast.success('Table queued', `${name} will be created when you click "Add to Pipeline".`);
    this.closeCreateTableModal();
  }

  // Removing a table drops any mappings already made onto it, so it's confirmed first rather than
  // acting immediately on click — matching the wizard's own confirm-before-discarding pattern. Covers
  // both an extra table and the primary one (its own "✕" clears the resource's target instead of
  // filtering extraTables, since the primary slot isn't a member of that list).
  readonly pendingRemoveTable = signal<{ resource: string; tableName: string; isExtra: boolean } | null>(null);

  /** Mongo-only inline rename (see FieldMappingTargetCardComponent.startRenameTable) — unlike remove +
   *  re-add, this keeps every mapping already made onto this table/collection: they're carried over to the
   *  new name (mapped by tableName), never filtered out the way confirmRemoveTable() discards them. */
  onRenameTable(resource: string, oldName: string, newName: string, isExtra: boolean): void {
    const trimmed = newName.trim();
    if (!trimmed || trimmed === oldName) return;
    if (this.targetFor(resource) === trimmed || this.extraTables().includes(trimmed)) {
      this.toast.warning('Name already used', `${trimmed} is already on this canvas.`);
      return;
    }

    if (isExtra) {
      this.extraTablesChange.emit(this.extraTables().map(t => (t === oldName ? trimmed : t)));
    } else {
      this.targetByResourceChange.emit({ ...this.targetByResource(), [resource]: trimmed });
    }

    this.mappingRowsChange.emit(
      this.mappingRows().map(r =>
        r.resource === resource && r.tableName === oldName ? { ...r, tableName: trimmed } : r,
      ),
    );
    // Any schema op queued against the old name (SQL-only in practice — Mongo never queues one) is stale now.
    this.schemaOpsCancelledForTable.emit(oldName);
    this.toast.success('Collection renamed', `${oldName} is now ${trimmed}.`);
  }

  onRemoveTable(resource: string, tableName: string, isExtra: boolean): void {
    this.pendingRemoveTable.set({ resource, tableName, isExtra });
  }

  cancelRemoveTable(): void {
    this.pendingRemoveTable.set(null);
  }

  confirmRemoveTable(): void {
    const pending = this.pendingRemoveTable();
    if (!pending) return;
    const { resource, tableName, isExtra } = pending;

    if (isExtra) {
      // The parent (DestinationWizardComponent.onExtraTablesChange) discards mappings onto the removed
      // table itself once it sees it drop out of the list — no need to also filter mappingRows here.
      this.extraTablesChange.emit(this.extraTables().filter(t => t !== tableName));
    } else {
      // Unlike extraTablesChange, the parent's targetByResourceChange handler is a bare signal.set() with
      // no cleanup of its own, so this table's mappings are discarded here before clearing the target —
      // otherwise they'd silently survive, orphaned against a target the resource no longer points at.
      this.mappingRowsChange.emit(
        this.mappingRows().filter(r => !(r.resource === resource && r.tableName === tableName)),
      );
      this.targetByResourceChange.emit({ ...this.targetByResource(), [resource]: '' });
    }

    // Whether this table was ever queued for real this session (schemaAuthoringEnabled=false, or a real
    // pre-existing table nobody created here, both leave nothing to cancel) or not, filtering by name is
    // harmless — the parent's pendingSchemaOps is just a no-op filter if there was never a matching op.
    this.schemaOpsCancelledForTable.emit(tableName);
    this.pendingRemoveTable.set(null);
  }

  // ── delete column (real, irreversible ALTER TABLE ... DROP COLUMN) ──────
  // Only a column that's actually real (part of a live-probed SQL schema) needs the real, destructive
  // backend call + confirmation. A CSV column, or a SQL column that's only ever existed as a local
  // free-text placeholder (never-probed connection), isn't a real column to drop in the first place —
  // deleting it is just a local un-mapping, same as it already works for those cases elsewhere.
  readonly pendingDropColumn = signal<{ resource: string; tableName: string; column: string } | null>(null);
  readonly dropColumnSubmitting = signal(false);

  onDeleteColumn(resource: string, tableName: string, column: string): void {
    // hasSqlTables() already implies isSql() (sql/mysql/postgres) — see its own definition — so the
    // extra destType() === 'sql' this used to require silently downgraded MySQL/PostgreSQL's real,
    // schema-mutation-backed drop into a local-only un-mapping that never touched the real table.
    if (this.hasSqlTables()) {
      this.pendingDropColumn.set({ resource, tableName, column });
      return;
    }
    this.removeRow(resource, tableName, column);
    this.unregisterPendingColumn(resource, tableName, column);
  }

  cancelDropColumn(): void {
    if (this.dropColumnSubmitting()) return;
    this.pendingDropColumn.set(null);
  }

  confirmDropColumn(): void {
    const target = this.pendingDropColumn();
    const connection = this.connectionInfo();
    if (!target || !connection) return;

    this.columnDropped.emit({ tableName: target.tableName, column: target.column });
    this.removeRow(target.resource, target.tableName, target.column);
    this.schemaOpQueued.emit({
      kind: 'dropColumn',
      request: { connection, tableName: target.tableName, columnName: target.column },
    });
    this.toast.success('Column queued for removal', `${target.column} will be dropped from ${target.tableName} when you click "Add to Pipeline".`);
    this.pendingDropColumn.set(null);
  }

  /** Mirrors SqlDestinationSchemaService.MapSqlServerType — used only to render an accurate local preview
   *  of a column's mappingValueType before the real DDL runs (see schemaOpQueued); the deferred flush
   *  later overwrites this with whatever the backend's own response says once it actually executes. */
  private mapSqlServerType(dataType: string): string {
    const family = dataType.trim().toLowerCase().split('(')[0];
    switch (family) {
      case 'bit': return 'Boolean';
      case 'tinyint': case 'smallint': case 'int': case 'bigint': return 'Integer';
      case 'decimal': case 'numeric': case 'money': case 'smallmoney': case 'float': case 'real': return 'Decimal';
      case 'date': return 'Date';
      case 'datetime': case 'datetime2': case 'datetimeoffset': case 'smalldatetime': return 'DateTime';
      default: return 'String';
    }
  }

  private unregisterPendingColumn(resource: string, tableName: string, column: string): void {
    const key = `${resource}::${tableName}`;
    this.updatePendingFreeColumns(m => ({ ...m, [key]: (m[key] ?? []).filter(c => c !== column) }));
  }

  // ── add column (real ALTER TABLE) ───────────────────────────────────────
  readonly addColumnTarget = signal<{ resource: string; tableName: string } | null>(null);
  readonly addColumnSubmitting = signal(false);
  readonly addColumnError = signal<string | null>(null);

  openAddColumnModal(resource: string, tableName: string): void {
    if (!this.schemaAuthoringEnabled()) return; // defensive — the triggering UI is hidden below when disabled
    this.addColumnTarget.set({ resource, tableName });
    this.addColumnError.set(null);
  }

  closeAddColumnModal(): void {
    if (this.addColumnSubmitting()) return;
    this.addColumnTarget.set(null);
    this.addColumnError.set(null);
  }

  submitAddColumn(submission: FmAddColumnSubmit): void {
    const target = this.addColumnTarget();
    const connection = this.connectionInfo();
    if (!target || !connection) return;

    // Refuse up front rather than queuing an op that's already guaranteed to fail once flushed —
    // AddColumnAsync deliberately never auto-creates a missing table (see its own doc comment: "Never
    // auto-create the table here"), so this canvas no longer pretends otherwise with an optimistic
    // phantom-table preview either. See field-mapping-schema-ops.util.ts's canQueueAddColumn for exactly
    // what "already known" means here: a real table, or one already staged via a pending "Create a new
    // table…" this session (in which case the real CREATE runs immediately before this ADD COLUMN when
    // the queue flushes — see runQueuedOpsSequentially).
    // MySQL-only bare-name fallback — see sqlTableNames' doc comment above. sqlTableOptions() is always the
    // live-probed, database-qualified name for MySQL (e.g. "fhirbridge_output.Patient"), while a restored
    // target from a saved mapping is the bare name (e.g. "Patient") — without this, a perfectly real,
    // already-visible MySQL table is refused here as "does not exist yet". canQueueAddColumn() itself stays
    // untouched; this only widens the list of names it's allowed to consider already-known, for MySQL only.
    const knownTableNames = this.destType() === 'mysql'
      ? [...this.sqlTableOptions(), ...this.sqlTableNames()]
      : this.sqlTableOptions();
    if (!canQueueAddColumn(target.tableName, knownTableNames, this.pendingTableNames())) {
      this.addColumnError.set(`${target.tableName} does not exist yet. Create it first via "Create a new table…", then add columns to it.`);
      return;
    }

    const column: DestinationColumn = {
      name: submission.columnName,
      dataType: submission.dataType,
      mappingValueType: this.mapSqlServerType(submission.dataType),
      isNullable: true,
      maxLength: null,
      origin: 'userCreated',
    };

    // The guard above guarantees this table is already represented on the canvas one way or another (a
    // real probed table, or a still-pending "Create a new table…" whose own optimistic preview already
    // registered it) — so this is always a plain append onto an existing table, never a fabricated new
    // one. See onColumnAdded (destination-wizard.component.ts): its no-`table` branch upserts just this
    // one column into the table it already knows about, without disturbing any other column already
    // staged on it (see submitCreateTable's own columns, or an earlier submitAddColumn's).
    this.columnAdded.emit({ tableName: target.tableName, column });
    if (!this.hasSqlTables()) this.registerPendingColumn(target.resource, target.tableName, submission.columnName);
    this.schemaOpQueued.emit({
      kind: 'addColumn',
      request: { connection, tableName: target.tableName, columnName: submission.columnName, dataType: submission.dataType },
    });
    this.toast.success('Column queued', `${submission.columnName} will be added to ${target.tableName} when you click "Add to Pipeline".`);
    this.addColumnTarget.set(null);
  }

  // ── default value (a column that always writes a fixed value, never a source mapping) ──────────
  /** `existing` is captured ONCE, right here at open time — not recomputed reactively from a template
   *  method call (which would hand the modal a freshly-allocated object every change-detection tick, no
   *  two of which are ===, even though logically unchanged). That mattered twice over: a resync-on-every-
   *  input-change effect inside the modal would fight every keystroke the user types into the literal
   *  field, undoing it back to the saved value; and, without a resync at all (the modal's own token/
   *  literalValue/valueType signals only ever read `existing()` in their field initializers), an `existing`
   *  input that hadn't settled to the real row's value by the time those initializers ran left the modal
   *  permanently blank on open — the exact "Edit doesn't reload the value" bug this fixes. Snapshotting
   *  once up front sidesteps needing to reason about Angular's exact input-vs-constructor timing at all. */
  readonly defaultValueTarget = signal<{
    resource: string; tableName: string; column: string; existing: FmDefaultValueSubmit | null;
  } | null>(null);

  openDefaultValueModal(resource: string, tableName: string, column: string): void {
    const row = this.rowForColumnFn(resource, tableName, column);
    const existing: FmDefaultValueSubmit | null = row?.mode === 'default'
      ? { token: row.defaultToken ?? '@default', literalValue: row.defaultValue ?? null, valueType: row.defaultValueType ?? 'String' }
      : null;
    this.defaultValueTarget.set({ resource, tableName, column, existing });
  }

  closeDefaultValueModal(): void {
    this.defaultValueTarget.set(null);
  }

  /** Wholesale-replaces whatever this column had (a real source mapping, a childJson group, or nothing at
   *  all) with a pure default row — no `sources`, so there's structurally nothing left for the column to
   *  also be mapped from. Dragging a new source onto this column later replaces it right back (see
   *  replaceRow), which is the other half of what keeps the two mutually exclusive. */
  submitDefaultValue(submission: FmDefaultValueSubmit): void {
    const target = this.defaultValueTarget();
    if (!target) return;
    const { resource, tableName, column } = target;
    this.replaceRow(resource, tableName, column, {
      resource,
      sources: [],
      mode: 'default',
      targetName: column,
      tableName,
      defaultToken: submission.token,
      defaultValue: submission.token === '@default' ? submission.literalValue : null,
      defaultValueType: submission.valueType,
    });
    this.toast.success('Default value set', `${column} will always write this value.`);
    this.defaultValueTarget.set(null);
  }

  /** "Clear default" in the modal — drops the row entirely, back to unmapped (same effect as the
   *  column's own 🗑, but reachable from the modal that's already open). */
  removeDefaultValue(): void {
    const target = this.defaultValueTarget();
    if (!target) return;
    this.removeRow(target.resource, target.tableName, target.column);
    this.defaultValueTarget.set(null);
  }

  // ── edit column (real ALTER TABLE ... ALTER COLUMN, + sp_rename if the name changes) ───────────
  readonly editColumnTarget = signal<{ resource: string; tableName: string; columnName: string } | null>(null);
  readonly editColumnSubmitting = signal(false);
  readonly editColumnError = signal<string | null>(null);

  openEditColumnModal(resource: string, tableName: string, columnName: string): void {
    this.editColumnTarget.set({ resource, tableName, columnName });
    this.editColumnError.set(null);
  }

  closeEditColumnModal(): void {
    if (this.editColumnSubmitting()) return;
    this.editColumnTarget.set(null);
    this.editColumnError.set(null);
  }

  submitEditColumn(submission: FmEditColumnSubmit): void {
    const target = this.editColumnTarget();
    const connection = this.connectionInfo();
    if (!target || !connection) return;

    // ALTER COLUMN always preserves the existing NULL/NOT NULL constraint server-side (see
    // AlterColumnRequest's doc comment) — carry over whatever's already known locally (PK/FK/nullable)
    // for the same reason, rather than guessing new values the real flush would just overwrite anyway.
    const existing = this.keyInfoForTable()(target.tableName, target.columnName);
    const column: DestinationColumn = {
      name: submission.newColumnName || target.columnName,
      dataType: submission.newDataType,
      mappingValueType: this.mapSqlServerType(submission.newDataType),
      isNullable: existing?.isNullable ?? true,
      maxLength: null,
      isPrimaryKey: existing?.isPrimaryKey,
      isForeignKey: existing?.isForeignKey,
      references: existing?.references,
      origin: existing?.origin ?? 'userCreated',
    };
    this.columnAltered.emit({ tableName: target.tableName, oldColumnName: target.columnName, column });
    this.schemaOpQueued.emit({
      kind: 'alterColumn',
      request: {
        connection,
        tableName: target.tableName,
        columnName: target.columnName,
        newColumnName: submission.newColumnName,
        newDataType: submission.newDataType,
      },
    });
    this.toast.success('Column update queued', `${target.columnName} → ${column.name} on ${target.tableName} applies when you click "Add to Pipeline".`);
    this.editColumnTarget.set(null);
  }

  // ── load source payload (paste real FHIR JSON, rebuild the source tree from its actual shape) ──
  readonly loadPayloadOpen = signal(false);
  readonly loadPayloadError = signal<string | null>(null);

  openLoadPayloadModal(): void {
    this.loadPayloadOpen.set(true);
    this.loadPayloadError.set(null);
  }

  closeLoadPayloadModal(): void {
    this.loadPayloadOpen.set(false);
    this.loadPayloadError.set(null);
  }

  /** Whatever this resource's source tree is ACTUALLY showing right now — reconstructed fresh (not
   *  cached) from availableFields(), the exact same override-aware field list `forest` itself is built
   *  from (see the computed just above). Pre-fills the Load JSON Payload modal's textarea every time it
   *  opens, so re-opening it always shows the CURRENT payload — a previously loaded/edited one if there is
   *  one, the true default catalog otherwise — never a stale default unrelated to what's on screen. See
   *  originalPayloadJsonFor just below for the separate, override-FREE "true original" reconstruction. */
  currentPayloadJsonFor(resource: string): string {
    if (!resource) return '';
    return reconstructPayloadJsonFor(resource, this.availableFields()(resource));
  }

  /** The TRUE original/default JSON payload for `resource` — reconstructed fresh (not cached) from
   *  defaultAvailableFields(), which is deliberately override-free (see its own doc comment): the real
   *  backend FHIR catalog, or the built-in defs until that loads — NEVER whatever a previously-pasted
   *  payload already replaced it with. This is what "Reset to Original" restores back to — deliberately
   *  separate from currentPayloadJsonFor just above, which DOES reflect any such override, precisely so
   *  the two can't collapse into "always shows the same thing" the way a single shared source would. */
  originalPayloadJsonFor(resource: string): string {
    if (!resource) return '';
    return reconstructPayloadJsonFor(resource, this.defaultAvailableFields()(resource));
  }

  /** Drops every existing mapping for `resource` — used by both submitLoadPayload (a genuinely new
   *  payload shape just replaced this resource's fields; any row still pointing at the old shape's
   *  fhirPaths would be silently dangling) and onResetToOriginalPayload (the Load JSON Payload modal's
   *  own "Reset to Original", which clears the same way even though nothing was actually re-parsed here).
   *  Every OTHER resource's rows are left completely untouched, same as removeRow's own identity filter. */
  private clearMappingsForResource(resource: string): void {
    this.mappingRowsChange.emit(this.mappingRows().filter(r => r.resource !== resource));
  }

  submitLoadPayload(raw: string): void {
    const resource = this.resources()[0];
    if (!resource) return;

    const result = parseSourcePayloadJson(resource, raw);
    if (!result.ok) {
      this.loadPayloadError.set(result.error);
      return;
    }

    this.sourcePayloadLoaded.emit({ resource, fields: result.fields });
    this.clearMappingsForResource(resource);
    this.loadPayloadOpen.set(false);
    this.loadPayloadError.set(null);
    const count = `${result.fields.length} field${result.fields.length === 1 ? '' : 's'}`;
    this.toast.success(
      'Payload loaded',
      result.declaredResourceType
        ? `${count} found (pasted JSON declares resourceType "${result.declaredResourceType}") — previous mappings for ${resource} were cleared.`
        : `${count} found in the pasted ${resource} JSON — previous mappings for ${resource} were cleared.`,
    );
  }

  /** "Reset to Original" in the Load JSON Payload modal — the textarea reset itself is entirely local to
   *  that component (it just re-reads its own originalRaw input, see
   *  FieldMappingLoadPayloadModalComponent.onResetToOriginal); this handles the two things the modal
   *  can't do itself: clearing this resource's existing mappings (same as a real submit does), and telling
   *  the parent to drop its pasted-payload override (sourcePayloadReset — payloadFieldsByResource lives on
   *  the parent) so the SOURCE TREE itself also reverts to the true default catalog, not just the modal's
   *  own textarea. */
  onResetToOriginalPayload(): void {
    const resource = this.resources()[0];
    if (!resource) return;
    this.clearMappingsForResource(resource);
    this.sourcePayloadReset.emit(resource);
    this.loadPayloadError.set(null);
    this.toast.info('Reset to original', `Mappings for ${resource} were cleared.`);
  }

  // ── load destination payload (paste the target system's own JSON body, rebuild this card's column
  // list from its actual shape) — the destination-side counterpart to "Load JSON payload" above. Only
  // ever offered for a file-shaped card (see field-mapping-target-card.component.html's isSqlFamily()
  // branch: a real SQL table's columns come from a live schema probe, not free text) ─────────────────
  readonly loadDestinationPayloadTarget = signal<{ resource: string; tableName: string } | null>(null);
  readonly loadDestinationPayloadError = signal<string | null>(null);
  readonly loadDestinationPayloadSubmitting = signal(false);

  openLoadDestinationPayloadModal(resource: string, tableName: string): void {
    this.loadDestinationPayloadTarget.set({ resource, tableName });
    this.loadDestinationPayloadError.set(null);
  }

  closeLoadDestinationPayloadModal(): void {
    this.loadDestinationPayloadTarget.set(null);
    this.loadDestinationPayloadError.set(null);
  }

  submitLoadDestinationPayload(raw: string): void {
    const target = this.loadDestinationPayloadTarget();
    if (!target) return;

    const result = parseDestinationPayloadJson(raw);
    if (!result.ok) {
      this.loadDestinationPayloadError.set(result.error);
      return;
    }

    const { resource, tableName } = target;
    const key = `${resource}::${tableName}`;
    // A fresh column list reflecting the newly pasted structure. Unlike a full wipe, a mapping row is only
    // dropped when its OWN target column is no longer part of the new structure — re-pasting JSON to tweak
    // or add one field must not silently discard every other already-configured mapping on this card, which
    // is exactly what re-loading used to do (every column's row was dropped regardless of whether that
    // column still existed afterward). A row whose column survives the reload keeps its mapping untouched.
    const newColumns = Array.from(new Set(result.columns));
    this.updatePendingFreeColumns(m => ({ ...m, [key]: newColumns }));
    const newColumnSet = new Set(newColumns);
    const rowsBefore = this.mappingRows();
    const droppedCount = rowsBefore.filter(
      r => r.resource === resource && r.tableName === tableName && !newColumnSet.has(r.targetName)).length;
    this.mappingRowsChange.emit(
      rowsBefore.filter(r => !(r.resource === resource && r.tableName === tableName) || newColumnSet.has(r.targetName)));
    this.destinationTemplateGenerated.emit({ resource, templateJson: result.templateJson });

    this.loadDestinationPayloadTarget.set(null);
    this.loadDestinationPayloadError.set(null);
    const count = `${result.columns.length} column${result.columns.length === 1 ? '' : 's'}`;
    const droppedNote = droppedCount > 0
      ? ` ${droppedCount} mapping${droppedCount === 1 ? '' : 's'} for column${droppedCount === 1 ? '' : 's'} no longer in the new structure ${droppedCount === 1 ? 'was' : 'were'} dropped.`
      : ' Existing mappings for columns still present were kept.';
    this.toast.success(
      'Payload loaded',
      `${count} found in the pasted JSON for ${tableName || resource}.${droppedNote} `
        + `Drag each source field onto any new column, then Save — the request body template updates itself.`
        + (result.note ? ` ${result.note}` : ''),
    );
  }

  // ── rank color per resource ─────────────────────────────────────────────
  resourceColorVarFn = (resource: string): string => {
    const i = this.resources().indexOf(resource);
    return `var(--fm-rank-${((i < 0 ? 0 : i) % 11) + 1})`;
  };

  // ── drag interaction ─────────────────────────────────────────────────────
  onSourceDragStart(e: FmDragStart): void {
    this.dragSourceId = e.node.id;
    this.dragSourceKind = e.node.kind;
    this.dragTempWire.set({
      fromId: e.node.id, toClientX: e.clientX, toClientY: e.clientY,
      stroke: this.resourceColorVarFn(e.node.resource),
    });
  }

  onSourceDragMove(e: FmDragMove): void {
    const current = this.dragTempWire();
    if (!current) return;
    this.dragTempWire.set({ ...current, toClientX: e.clientX, toClientY: e.clientY });
  }

  onSourceDragEnd(e: FmDragEnd): void {
    const sourceId = this.dragSourceId;
    const kind = this.dragSourceKind;
    this.dragTempWire.set(null);
    this.dragSourceId = null;
    this.dragSourceKind = null;
    if (!sourceId || !kind) return;

    const dropEl = document.elementFromPoint(e.clientX, e.clientY)?.closest('[data-fm-drop]');
    const dropKey = dropEl?.getAttribute('data-fm-drop');
    if (!dropKey) return;
    const parts = dropKey.split('::');
    if (parts.length !== 3) return;
    const [resource, tableName, column] = parts;

    if (column === FieldMappingCanvasComponent.NEW_FREE_COLUMN_DROP_KEY) {
      // Dropped straight onto an empty free-text card — create the column (named from the dragged
      // field/group itself) and map onto it in one motion, instead of requiring "type a name, then drag"
      // as two separate steps.
      const name = this.autoColumnNameFor(sourceId, kind);
      if (!this.columnsForCardFn(resource, tableName, false).includes(name)) {
        this.registerPendingColumn(resource, tableName, name);
      }
      this.completeMapping(sourceId, kind, resource, tableName, name);
      return;
    }

    this.completeMapping(sourceId, kind, resource, tableName, column);
  }

  /** Column name auto-derived from a dragged source field/group, for dropping straight onto an empty
   *  free-text card (see NEW_FREE_COLUMN_DROP_KEY) — same PascalCase-from-path convention already used
   *  for sqlColumn/csvColumn (see DestinationWizardComponent._toFieldDef / field-mapping-payload.util.ts's
   *  pushLeaf), so a column created this way looks exactly like one the built-in catalog would have named. */
  private autoColumnNameFor(sourceId: string, kind: 'group' | 'leaf'): string {
    const node = findNode(this.forest(), sourceId);
    const path = kind === 'leaf' ? (node?.field?.fhirPath ?? sourceId) : (node?.label ?? sourceId);
    return path.split(/[.\s]+/).filter(Boolean).map(s => s.charAt(0).toUpperCase() + s.slice(1)).join('') || 'Value';
  }

  // ── keyboard arm-and-target ──────────────────────────────────────────────
  onArmToggle(node: FmTreeNode): void {
    this.armedId.set(this.armedId() === node.id ? null : node.id);
    this.armedKind = this.armedId() ? node.kind : null;
  }

  onColumnActivate(resource: string, tableName: string, column: string): void {
    const sourceId = this.armedId();
    const kind = this.armedKind;
    if (!sourceId || !kind) return;
    this.completeMapping(sourceId, kind, resource, tableName, column);
    this.armedId.set(null);
    this.armedKind = null;
  }

  // ── mapping mutation ─────────────────────────────────────────────────────
  /** True for a column the database (or the mapping engine's own child-table relationship resolution)
   *  fills in on its own — an identity/computed column (isAutoGenerated) or a foreign key populated from
   *  its declared parent relationship (isForeignKey) at write time, never from a payload field a user
   *  drags in. Deliberately NOT baked into columnsForCardFn/columnsForTable/columnsForResourceTarget —
   *  those still return every real column so the target card keeps showing a table's true shape
   *  (including its PK/FK badges); this is checked at the point a mapping is actually completed instead,
   *  so the column stays visible but can't become a target. Undefined keyInfo (CSV, or a free-text
   *  column with no real schema yet) is never protected — there's nothing there for the database or a
   *  relationship to auto-populate. */
  private isProtectedColumn(tableName: string, column: string): boolean {
    const info = this.keyInfoForTable()(tableName, column);
    return !!(info?.isAutoGenerated || info?.isForeignKey);
  }

  /** Same list as columnsForCardFn, minus whatever isProtectedColumn excludes — used by every surface
   *  that lets a user PICK a column to map onto (the mapping-list drawer's add-row form, Suggest
   *  Mappings' candidate pool) so those never even offer one. columnsForCardFn itself stays unfiltered
   *  and is still what the target card's own [columns] rendering calls directly. */
  private mappableColumnsForCardFn = (resource: string, tableName: string, isExtra: boolean): string[] =>
    this.columnsForCardFn(resource, tableName, isExtra).filter(c => !this.isProtectedColumn(tableName, c));

  private completeMapping(sourceId: string, kind: 'group' | 'leaf', resource: string, tableName: string, column: string): void {
    if (this.isProtectedColumn(tableName, column)) {
      const info = this.keyInfoForTable()(tableName, column);
      const reason = info?.isAutoGenerated
        ? 'is generated automatically by the database'
        : `is populated automatically from its ${info?.references ? info.references.split('.').slice(0, -1).join('.') : 'parent'} relationship`;
      this.toast.warning('Cannot map this column', `"${column}" on ${tableName} ${reason} and can't be a mapping target.`);
      return;
    }

    const existing = this.rowForColumnFn(resource, tableName, column);

    if (kind === 'group') {
      const replaced = existing != null;
      this.replaceRow(resource, tableName, column, {
        resource, sources: [], mode: 'childJson', childNodeId: sourceId,
        targetName: column, tableName,
      });
      this.toast.success('Mapped whole node', `${sourceId} → ${column}${replaced ? ' (replaced previous mapping)' : ''} as JSON.`);
      return;
    }

    const leaf = findNode(this.forest(), sourceId);
    const source: MappingSourceRef = {
      fhirPath: sourceId,
      label: leaf?.label ?? sourceId,
      jsonPath: leaf?.field?.jsonPath,
      valueType: leaf?.field?.valueType,
      arrays: leaf?.field?.arrays,
    };

    if (!existing) {
      this.replaceRow(resource, tableName, column, {
        resource, sources: [source], mode: 'value', instance: { type: 'first' },
        targetName: column, tableName,
      });
      this.toast.info('Mapped', `${source.label} → ${column}`);
      return;
    }

    if (existing.mode === 'value') {
      if (existing.sources.some(s => s.fhirPath === sourceId)) {
        this.toast.info('Already mapped', `${source.label} is already part of this mapping.`);
        return;
      }
      const joined: MappingRow = {
        ...existing,
        sources: [...existing.sources, source],
        delimiter: existing.delimiter ?? ', ',
      };
      this.replaceRow(resource, tableName, column, joined);
      this.toast.info('Joined 2 sources', `Configure order & delimiter for ${column}.`);
      this.popoverKey.set({ resource, tableName, targetName: column });
      return;
    }

    // existing.mode === 'childJson' — a leaf drop takes precedence over a whole-node mapping.
    this.replaceRow(resource, tableName, column, {
      resource, sources: [source], mode: 'value', instance: { type: 'first' },
      targetName: column, tableName,
    });
    this.toast.info('Replaced', `${source.label} → ${column} (replaced the whole-node JSON mapping).`);
  }

  private replaceRow(resource: string, tableName: string, column: string, row: MappingRow): void {
    const rows = this.mappingRows().filter(r => !(r.resource === resource && r.tableName === tableName && r.targetName === column));
    rows.push(row);
    this.mappingRowsChange.emit(rows);
  }

  removeRow(resource: string, tableName: string, column: string): void {
    this.mappingRowsChange.emit(
      this.mappingRows().filter(r => !(r.resource === resource && r.tableName === tableName && r.targetName === column)),
    );
  }

  /** "All records" without combining (RepeatParent) duplicates the entire destination row once per array
   *  item — correct only for a genuine child/array destination table, where this choice doesn't even matter
   *  (serializeRowsFlat overrides the array policy to SeparateDestination there regardless). On any other
   *  (same-table) target it duplicates the whole row per array item, which is essentially always wrong — so
   *  "All records" always combines into one delimited string, with no way to opt out via the UI. Enforced
   *  here (the single choke point both the wire-click popover's Save and the mapping-list row's own "All
   *  records"/combine controls funnel through) rather than in either UI separately, so neither can drift out
   *  of sync with the other. */
  private withForcedAggregate(row: MappingRow): MappingRow {
    if (row.instance?.type !== 'all' || row.instance.aggregate === 'csv') return row;
    return { ...row, instance: { ...row.instance, aggregate: 'csv' } };
  }

  updateRow(updated: MappingRow): void {
    const normalized = this.withForcedAggregate(updated);
    this.mappingRowsChange.emit(
      this.mappingRows().map(r =>
        (r.resource === normalized.resource && r.tableName === normalized.tableName && r.targetName === normalized.targetName) ? normalized : r,
      ),
    );
  }

  /** Only one row per (resource, table/collection) can be the upsert key — the backend resolves one key
   *  column per DestinationObject, independently for each one (see MappedSqlServerDestinationWriter's and
   *  MappedMongoDestinationWriter's own ResolveUpsertKeyField, both scoped by DestinationObject, not just
   *  resource) — so marking one on only clears any other explicit key already set for the SAME table, never
   *  a key set on a different collection for this resource (e.g. the primary and an independently-added
   *  extra Mongo collection each keep their own key). Marking the already-active row off drops the explicit
   *  override entirely, reverting that table back to the real-PK fallback in serializeRowsFlat. */
  onToggleUpsertKey(resource: string, tableName: string, column: string): void {
    const target = this.rowForColumnFn(resource, tableName, column);
    if (!target) return;
    const turningOn = !target.isUpsertKey;
    this.mappingRowsChange.emit(
      this.mappingRows().map(r => {
        if (r.resource !== resource || r.tableName !== tableName) return r;
        if (r === target) return { ...r, isUpsertKey: turningOn };
        return r.isUpsertKey ? { ...r, isUpsertKey: false } : r;
      }),
    );
  }

  // ── popover / drawer ─────────────────────────────────────────────────────
  /** The full, real MappingRow behind the current popoverKey — unfiltered, always every source. This is
   *  the row identity/save-target lookup; the popover itself is shown popoverDisplayRow below, which may
   *  be a narrowed view of this same row. */
  popoverRow = computed<MappingRow | null>(() => {
    const key = this.popoverKey();
    if (!key) return null;
    return this.rowForColumnFn(key.resource, key.tableName, key.targetName) ?? null;
  });

  /** What actually gets passed to <app-field-mapping-join-popover>'s `row` input. Identical to
   *  popoverRow() unless popoverKey.focusSourceFhirPath names one specific source of a genuine join
   *  (mode 'value', more than one entry in `sources`) — in that case, returns a shallow view with
   *  `sources` narrowed to just that one entry. The popover's own "Source fields" section, delimiter
   *  box, and chip reorder/remove controls already render purely off however many entries `sources` has,
   *  so a 1-source view renders — and behaves — exactly like an ordinary non-join mapping's popover
   *  always has; it's also told the REAL total via [totalSourceCount] (see the template), purely so it
   *  can offer "Show all N sources" back to the full row (onShowAllSources) when it's showing a narrowed
   *  view. See onPopoverSave for the other half of this (merging edits made against this narrowed view
   *  back into the real row's full source list). */
  readonly popoverDisplayRow = computed<MappingRow | null>(() => {
    const row = this.popoverRow();
    const focus = this.popoverKey()?.focusSourceFhirPath;
    if (!row || !focus || row.mode !== 'value' || row.sources.length <= 1) return row;
    const source = row.sources.find(s => s.fhirPath === focus);
    return source ? { ...row, sources: [source] } : row;
  });

  /** `e.sourceFhirPath` is the ACTUAL source the clicked wire was drawn for (FmWirePath.sourceFhirPath,
   *  resolved by field-mapping-wires.component.ts's own `paths` computed directly from that row's real
   *  `sources[]` — never guessed from the wire's on-screen position), so a join's individual connector
   *  lines each open focused on their own specific source → target relationship instead of the whole
   *  join every one of them happens to share a target with. */
  onWireClick(e: { resource: string; tableName: string; targetName: string; sourceFhirPath: string | null }): void {
    if (this.rowForColumnFn(e.resource, e.tableName, e.targetName)) {
      this.popoverKey.set({
        resource: e.resource, tableName: e.tableName, targetName: e.targetName,
        focusSourceFhirPath: e.sourceFhirPath,
      });
    }
  }

  // ── field click (source leaf or target column) -> open its Mapping Configuration directly, or let
  // the user choose which one when it's ambiguous ────────────────────────────────────────────────────
  // Two DIFFERENT kinds of ambiguity can be behind one field, and each needs its own granularity:
  //  - A single SOURCE field can be part of more than one MappingRow (e.g. "Name > Family" mapped both
  //    onto `name` and onto `PatientId` — two fully separate rows, each its own resource/tableName/
  //    targetName identity). Ambiguity here is ROW-granular: one picker option per matching row.
  //  - A single TARGET column can only ever back one MappingRow (replaceRow enforces that uniqueness —
  //    a join just means one row with multiple `sources`, never multiple rows), but that one row can
  //    still be fed by more than one source. Ambiguity here is SOURCE-granular: one picker option per
  //    entry in that row's `sources`, not one for the row as a whole — otherwise picking "the mapping
  //    for this column" would always show the whole join, which is exactly the confusing behavior this
  //    is meant to avoid (see popoverDisplayRow's doc comment above).
  // Either way, an option never carries more than resource/tableName/targetName + which one source to
  // focus on — never a new mapping identity, never a copy of the row itself.

  /** Every MappingRow with `sourceId` (a leaf's own id/fhirPath, or a group's id in childJson mode) as
   *  one of its sources. */
  private rowsForSourceId(sourceId: string): MappingRow[] {
    return this.mappingRows().filter(r =>
      r.mode === 'childJson' ? r.childNodeId === sourceId : r.sources.some(s => s.fhirPath === sourceId),
    );
  }

  /** "Patient.name.family" -> "Name > Family" — breadcrumb display for a source path/id, dropping the
   *  leading resource-type segment (redundant once already scoped to one resource's own cards). Used only
   *  for the field-picker's title/option labels — every other display of a source elsewhere in this
   *  feature keeps using its own existing label (MappingSourceRef.label, childNodeId, etc.) unchanged. */
  private sourceBreadcrumb(fhirPathOrId: string): string {
    const segments = fhirPathOrId.split('.').slice(1).filter(Boolean);
    if (!segments.length) return fhirPathOrId;
    return segments.map(s => s.charAt(0).toUpperCase() + s.slice(1)).join(' > ');
  }

  /** One "source → target" picker option for `row`, focused on `sourceFhirPath` — null only for a
   *  childJson row (nothing to single out; the whole row already is one atomic JSON mapping). */
  private toMappingChoice(row: MappingRow, sourceFhirPath: string | null): FmMappingChoice {
    const sourceLabel = sourceFhirPath ? this.sourceBreadcrumb(sourceFhirPath) : (row.childNodeId ?? '');
    return {
      resource: row.resource, tableName: row.tableName, targetName: row.targetName,
      sourceLabel, targetLabel: row.targetName, sourceFhirPath,
    };
  }

  /** Opens the exact same Mapping Configuration popup a connector-line click already opens (onWireClick)
   *  — same popoverKey/popoverRow identity, so nothing about how the popup itself works changes for this
   *  entry point. `focusSourceFhirPath` (see popoverDisplayRow) narrows what's actually SHOWN to one
   *  source when the row is a join and the click resolved to one specific source; left null to show the
   *  whole row, same as a connector-line click always has (harmless no-op when the row isn't a join —
   *  there's only ever one source to show either way). */
  private openMappingDirectly(row: MappingRow, focusSourceFhirPath: string | null): void {
    this.popoverKey.set({
      resource: row.resource, tableName: row.tableName, targetName: row.targetName, focusSourceFhirPath,
    });
  }

  /** Opens the "Select mapping" popover for a pre-built option list — both call sites below only ever
   *  call this once they already know there's more than one option, so an empty list never reaches here.
   *  `subtitle` is the fully composed second header line (see fieldPickerState's own doc comment). */
  private openFieldPicker(options: FmMappingChoice[], subtitle: string, anchorEl: HTMLElement): void {
    const rect = anchorEl.getBoundingClientRect();
    this.fieldPickerState.set({
      subtitle,
      options,
      style: this.computeFieldPickerStyle(rect),
    });
    this.suppressNextFieldPickerOutsideClick = true;
    // One tick so the panel (an @if-conditional sibling, just set into existence above) has actually
    // rendered before it's focused — same reasoning/preventScroll as toggleAddTableMenu's identical
    // pattern for its own search input.
    setTimeout(() => this.fieldPickerPanel()?.nativeElement.focus({ preventScroll: true }));
  }

  private static readonly FIELD_PICKER_WIDTH = 280;
  /** Hard cap on the WHOLE popup's height (fixed header + scrollable options together) — the header
   *  never grows past its own content, so in practice this is roughly how tall the options list can get
   *  before it starts scrolling internally instead of the panel just growing to fit every option (which
   *  is exactly how it used to run off the bottom of the canvas for 6+ mappings). */
  private static readonly FIELD_PICKER_MAX_HEIGHT = 300;
  /** Below this many px of room in a direction, that direction doesn't count as "enough space" to anchor
   *  the panel there at all — it would still show only a near-useless sliver. */
  private static readonly FIELD_PICKER_MIN_HEIGHT = 100;

  /**
   * Viewport-aware placement — replaces the previous version's `Math.max(minUsableHeight, spaceX)`,
   * which GUARANTEED at least ~120px of panel height regardless of how little room `spaceX` actually
   * measured (including negative, for a field right at the canvas's own edge) — that's exactly how the
   * panel ended up rendering past the visible boundary for a field near it, which is the bug reported.
   *
   * Vertical: tries below the clicked field first, falls back above when there's more (or only) room
   * there, and — only when NEITHER side has FIELD_PICKER_MIN_HEIGHT to offer — pins the panel fully
   * inside the usable rect instead of anchoring to either edge of the trigger at all. Whichever direction
   * is chosen, `maxHeight` is capped at FIELD_PICKER_MAX_HEIGHT AND never asked to exceed the space that
   * direction actually has, so the panel can never grow past either.
   *
   * Horizontal: opens to the right of the trigger by default (matching the previous behavior in the
   * common case), flips to the left of the trigger when the panel's full width doesn't fit on the right,
   * and only falls back to clamping against the usable rect's own edge when neither side fits the whole
   * width either.
   *
   * "Usable rect" is the intersection of `.fm-viewport`'s own box and the real browser window (a field
   * near the canvas's edge is bounded by whichever is smaller — the canvas panel can itself sit inside a
   * dialog well within the window). `panelWidth` is similarly clamped down from FIELD_PICKER_WIDTH if the
   * usable rect is ever narrower than that.
   *
   * The final absolute-to-CSS-offset conversion still goes through the same "escape the dialog's own
   * fixed-position containing block" probe technique as the "+ Add a table…" panel (see
   * toggleAddTableMenu's doc comment for why a plain viewport-relative fixed position is wrong here) —
   * that part is unchanged; only the space/placement math above it is new.
   */
  private computeFieldPickerStyle(
    triggerRect: DOMRect,
  ): { top?: number; bottom?: number; left: number; width: number; maxHeight: number } {
    const margin = 8;
    const viewportRect = this.viewport().nativeElement.getBoundingClientRect();
    const usable = {
      top: Math.max(viewportRect.top, 0),
      bottom: Math.min(viewportRect.bottom, window.innerHeight),
      left: Math.max(viewportRect.left, 0),
      right: Math.min(viewportRect.right, window.innerWidth),
    };

    const probe = document.createElement('div');
    probe.style.cssText = 'position:fixed; left:0; top:0; width:0; height:0; visibility:hidden;';
    this.viewport().nativeElement.appendChild(probe);
    const containingBlock = probe.offsetParent
      ? probe.offsetParent.getBoundingClientRect()
      : new DOMRect(0, 0, window.innerWidth, window.innerHeight);
    probe.remove();

    const panelWidth = Math.min(
      FieldMappingCanvasComponent.FIELD_PICKER_WIDTH,
      Math.max(0, usable.right - usable.left - margin * 2),
    );
    const maxHeightCap = FieldMappingCanvasComponent.FIELD_PICKER_MAX_HEIGHT;
    const minHeight = FieldMappingCanvasComponent.FIELD_PICKER_MIN_HEIGHT;

    // ── vertical: absolute (client-pixel) Y coordinates first, converted to CSS top/bottom offsets
    // (relative to containingBlock) only at the very end — exactly how `left` below already worked. ──
    const spaceBelow = usable.bottom - triggerRect.bottom - margin;
    const spaceAbove = triggerRect.top - usable.top - margin;

    let topAbs: number | undefined;
    let bottomAbs: number | undefined;
    let maxHeight: number;
    if (spaceBelow >= minHeight && spaceBelow >= spaceAbove) {
      topAbs = triggerRect.bottom + 6;
      maxHeight = Math.min(maxHeightCap, spaceBelow);
    } else if (spaceAbove >= minHeight) {
      bottomAbs = triggerRect.top - 6;
      maxHeight = Math.min(maxHeightCap, spaceAbove);
    } else {
      // Neither side has enough room next to the field itself — pin fully inside the usable rect instead
      // of anchoring to either edge of the trigger, sized to whatever vertical room genuinely exists.
      topAbs = usable.top + margin;
      maxHeight = Math.min(maxHeightCap, Math.max(0, usable.bottom - usable.top - margin * 2));
    }

    // ── horizontal: right of the trigger by default, flips left if the full width doesn't fit there,
    // else clamped fully inside the usable rect (the previous version's only strategy). ──
    const spaceRight = usable.right - triggerRect.left - margin;
    const spaceLeft = triggerRect.right - usable.left - margin;
    let leftAbs: number;
    if (spaceRight >= panelWidth) {
      leftAbs = triggerRect.left;
    } else if (spaceLeft >= panelWidth) {
      leftAbs = triggerRect.right - panelWidth;
    } else {
      leftAbs = Math.min(Math.max(usable.left + margin, triggerRect.left), usable.right - margin - panelWidth);
    }

    return {
      top: topAbs !== undefined ? topAbs - containingBlock.top : undefined,
      bottom: bottomAbs !== undefined ? containingBlock.bottom - bottomAbs : undefined,
      left: leftAbs - containingBlock.left,
      width: panelWidth,
      maxHeight,
    };
  }

  closeFieldPicker(): void {
    this.fieldPickerState.set(null);
  }

  /** A "Select mapping" option was clicked — opens the popup focused on that exact one option's source
   *  (see popoverDisplayRow), same as a direct (unambiguous) field click would have for it. */
  onFieldPickerChoice(choice: FmMappingChoice): void {
    this.popoverKey.set({
      resource: choice.resource, tableName: choice.tableName, targetName: choice.targetName,
      focusSourceFhirPath: choice.sourceFhirPath,
    });
    this.closeFieldPicker();
  }

  /** A source tree leaf was clicked (not dragged — see FieldMappingTreeNodeComponent.fieldClick). Groups
   *  never reach here (their plain click already means expand/collapse); a group's own childJson mapping
   *  stays reachable only via its connector line, same as today.
   *
   *  Ambiguity here is ROW-granular (see this section's own doc comment above): 0 rows reference this
   *  source -> nothing to open; exactly 1 -> open it directly, focused on this source (whether or not
   *  that row happens to be a join — harmless when it isn't); 2+ -> one picker option per row, each ALSO
   *  focused on this same source id, so picking one still shows only that one relationship, not whichever
   *  full join that row might be. */
  onSourceFieldClick(e: FmFieldClick): void {
    const sourceId = e.node.id;
    const matches = this.rowsForSourceId(sourceId);
    if (matches.length === 0) return;
    const focus = (row: MappingRow) => (row.mode === 'value' ? sourceId : null);
    if (matches.length === 1) {
      this.openMappingDirectly(matches[0], focus(matches[0]));
      return;
    }
    this.openFieldPicker(
      matches.map(r => this.toMappingChoice(r, focus(r))),
      `${matches.length} mappings for ${this.sourceBreadcrumb(sourceId)}`,
      e.anchor,
    );
  }

  /** A target card's column row was clicked. Always 0 or 1 MappingRow under the current data model (see
   *  this section's own doc comment above) — but that one row can still be a genuine join, in which case
   *  ambiguity is SOURCE-granular instead: one picker option per source feeding this column, each focused
   *  on its own source, rather than opening the whole join directly the way a single-row match otherwise
   *  would. A non-join row (or no row at all) behaves exactly as before. */
  onTargetFieldClick(resource: string, tableName: string, e: { column: string; anchor: HTMLElement }): void {
    const row = this.rowForColumnFn(resource, tableName, e.column);
    if (!row) return;
    // A 'default' row has no source to configure in the join popover (see openMappingDirectly's own
    // sources[0]-driven view) — its own edit surface is the default-value modal, not the picker.
    if (row.mode === 'default') {
      this.openDefaultValueModal(resource, tableName, e.column);
      return;
    }
    if (row.mode === 'value' && row.sources.length > 1) {
      this.openFieldPicker(
        row.sources.map(s => this.toMappingChoice(row, s.fhirPath)),
        `${row.sources.length} source mappings → ${e.column}`,
        e.anchor,
      );
      return;
    }
    this.openMappingDirectly(row, row.mode === 'value' ? (row.sources[0]?.fhirPath ?? null) : null);
  }

  onEditRow(e: { resource: string; tableName: string; targetName: string; invoker: HTMLElement }): void {
    const row = this.rowForColumnFn(e.resource, e.tableName, e.targetName);
    if (row?.mode === 'default') {
      this.openDefaultValueModal(e.resource, e.tableName, e.targetName);
      return;
    }
    this.popoverKey.set({ resource: e.resource, tableName: e.tableName, targetName: e.targetName });
  }

  onListAddRow(row: MappingRow): void {
    this.mappingRowsChange.emit([...this.mappingRows(), row]);
  }

  onListRemoveRow(e: { resource: string; tableName: string; targetName: string }): void {
    this.removeRow(e.resource, e.tableName, e.targetName);
  }

  /** Inline edits from the mapping list's own delimiter/instance controls (no popover needed) — same
   *  row-identity lookup as the popover path, just applied directly. */
  onListDelimiterChange(e: { resource: string; tableName: string; targetName: string; delimiter: string }): void {
    const row = this.rowForColumnFn(e.resource, e.tableName, e.targetName);
    if (row) this.updateRow({ ...row, delimiter: e.delimiter });
  }

  onListInstanceChange(e: { resource: string; tableName: string; targetName: string; instance: MappingInstanceSelection }): void {
    const row = this.rowForColumnFn(e.resource, e.tableName, e.targetName);
    if (row) this.updateRow({ ...row, instance: e.instance });
  }

  onListReferenceResourceChange(e: { resource: string; tableName: string; targetName: string; referencesResource: string | null }): void {
    const row = this.rowForColumnFn(e.resource, e.tableName, e.targetName);
    if (row) this.updateRow({ ...row, referencesResource: e.referencesResource ?? undefined });
  }

  /** Bumped every time the join popover closes — the "Transformations" tab's rule lookup (field-mapping-
   *  list) depends on this too, purely to know when to re-fetch; the popover can save/delete a
   *  transformation rule without ever touching a MappingRow, so nothing else here would otherwise tell
   *  that tab a rule just changed. Always bumped on close (not just on an actual save) since a cheap
   *  re-fetch on cancel/no-op close is simpler than tracking whether the popover's rule section was
   *  actually touched this time. */
  readonly ruleRefreshTrigger = signal(0);

  closePopover(): void {
    this.popoverKey.set(null);
    this.ruleRefreshTrigger.update(v => v + 1);
  }

  /**
   * `row` is whatever the popover emitted — its own `draft`, seeded from popoverDisplayRow(). When a
   * focus is active AND the real underlying row is a genuine join, that draft only ever contains the ONE
   * focused source (every other source was never even in it, deliberately hidden — see
   * popoverDisplayRow), so it must never be written back wholesale: doing so would silently drop every
   * other source from the join the instant the user hit Save, even if they changed nothing else. Instead,
   * re-fetch the CURRENT full row and splice just the focused source's entry back in:
   *  - still present in the draft (edited or not) -> replace that one entry in place, in its original
   *    position, leaving every other source untouched.
   *  - removed from the draft (the user clicked that one chip's own ✕ — the only way `sources` can come
   *    back empty from a 1-source draft) -> drop just that one entry from the join instead, same as
   *    removing one chip already does today when the WHOLE join is shown (e.g. via a connector-line
   *    click) — narrowing a join by one source has always meant exactly this, this just makes it work
   *    the same way when only that one chip was ever visible to begin with.
   * `instance`/`referencesResource` are the only other row-level fields this popover can actually change
   * (delimiter/reorder never apply to a 1-source view — see popoverDisplayRow) — carried over from the
   * draft; everything else (delimiter, isUpsertKey, isRequired, defaultValue, format, …) comes from the
   * real row, untouched, since the focused view never exposed them for editing in the first place.
   * A non-focused save (connector-line click, mapping-list Edit, or a row that was never a join to begin
   * with) is entirely unaffected — this only ever branches away from the original plain updateRow(row)
   * once BOTH a focus is active AND the real row actually has more than one source.
   */
  onPopoverSave(row: MappingRow): void {
    const focus = this.popoverKey()?.focusSourceFhirPath;
    const original = focus ? this.rowForColumnFn(row.resource, row.tableName, row.targetName) : undefined;
    if (focus && original && original.mode === 'value' && original.sources.length > 1) {
      const edited = row.sources[0];
      const sources = edited
        ? original.sources.map(s => (s.fhirPath === focus ? edited : s))
        : original.sources.filter(s => s.fhirPath !== focus);
      this.updateRow({
        ...original,
        instance: row.instance,
        referencesResource: row.referencesResource,
        jsonWriteMode: row.jsonWriteMode,
        sources,
      });
      this.closePopover();
      return;
    }
    this.updateRow(row);
    this.closePopover();
  }

  onPopoverRemove(): void {
    const key = this.popoverKey();
    if (key) this.removeRow(key.resource, key.tableName, key.targetName);
    this.closePopover();
  }

  /** "Show all sources" (field-mapping-join-popover's own link, shown only while a focus is narrowing a
   *  genuine join) — clears focusSourceFhirPath back to null without touching resource/tableName/
   *  targetName, so popoverDisplayRow falls back to the real, complete row again. Nothing is saved or
   *  mutated here; this only changes what's currently being VIEWED. */
  onShowAllSources(): void {
    const key = this.popoverKey();
    if (key) this.popoverKey.set({ ...key, focusSourceFhirPath: null });
  }

  openDrawer(): void { this.drawerOpen.set(true); }
  closeDrawer(): void { this.drawerOpen.set(false); }
}
