import {
  Component, ElementRef, HostListener, computed, effect, inject, input, output, signal, viewChild, AfterViewInit, OnDestroy, OnInit,
} from '@angular/core';
import type { ResourceFieldDef } from '../destination-wizard.component';
import { MappingRow, MappingSourceRef, MappingInstanceSelection, isApproximated, PendingSchemaOp, MappingDestType } from './field-mapping-model';
import { FmTreeNode, buildForest, findNode } from './field-mapping-tree.util';
import { MappingSuggestion, suggestMappings } from './field-mapping-automap.util';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { FieldMappingSourceTreeComponent } from './field-mapping-source-tree.component';
import { FieldMappingTargetCardComponent } from './field-mapping-target-card.component';
import { FieldMappingWiresComponent, FmTempWire } from './field-mapping-wires.component';
import { FmDragStart, FmDragMove, FmDragEnd } from './field-mapping-tree-node.component';
import { FieldMappingListComponent } from './field-mapping-list.component';
import { FieldMappingJoinPopoverComponent } from './field-mapping-join-popover.component';
import { FieldMappingPreviewDrawerComponent } from './field-mapping-preview-drawer.component';
import { FieldMappingAddColumnModalComponent, FmAddColumnSubmit } from './field-mapping-add-column-modal.component';
import { FieldMappingEditColumnModalComponent, FmEditColumnSubmit } from './field-mapping-edit-column-modal.component';
import { FieldMappingCreateTableModalComponent, FmCreateTableSubmit } from './field-mapping-create-table-modal.component';
import { FieldMappingLoadPayloadModalComponent } from './field-mapping-load-payload-modal.component';
import { parseSourcePayloadJson } from './field-mapping-payload.util';
import { ChildTableRelation } from './field-mapping-summary.model';
import { ToastService } from '../../../../services/toast.service';
import { DestinationColumn, DestinationTable, DestinationProbeRequest } from '../../../../services/destination-schema.service';

export interface FmTargetCardSpec {
  resource: string;
  tableName: string;
  isExtra: boolean;
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
  ],
  providers: [FieldMappingAnchorService],
  templateUrl: './field-mapping-canvas.component.html',
  styleUrl: './field-mapping-canvas.component.scss',
})
export class FieldMappingCanvasComponent implements OnInit, AfterViewInit, OnDestroy {
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly toast = inject(ToastService);
  private readonly canvasInner = viewChild.required<ElementRef<HTMLElement>>('canvasInner');
  private readonly viewport = viewChild.required<ElementRef<HTMLElement>>('viewport');
  // Not .required — only rendered while addTableMenuOpen() is true (see toggleAddTableMenu, which
  // focuses it manually once open instead of the `autofocus` attribute, which @angular-eslint/template/
  // no-autofocus disallows for the accessibility reasons in its own rule description).
  private readonly addTableSearchInput = viewChild<ElementRef<HTMLInputElement>>('addTableSearchInput');
  private resizeObserver: ResizeObserver | null = null;
  private viewportResizeObserver: ResizeObserver | null = null;

  readonly resources = input.required<string[]>();
  /** Every resource selected for this destination, NOT scoped down to the currently active mapping group
   *  (unlike `resources` above) — needed so a reference field's "Resolves to" picker can offer resources
   *  other than whichever one is presently being edited. */
  readonly allResources = input<string[]>([]);
  readonly destType = input.required<MappingDestType>();
  readonly mappingRows = input.required<MappingRow[]>();
  readonly targetByResource = input.required<Record<string, string>>();
  readonly availableFields = input.required<(r: string) => ResourceFieldDef[]>();
  readonly columnsForResourceTarget = input.required<(r: string) => string[]>();
  readonly hasSqlTables = input.required<boolean>();
  readonly sqlTableOptions = input.required<string[]>();
  readonly csvDelimiterKey = input<string>('comma');

  // Extra tables added alongside the resource's primary table — either picked from tables the SQL
  // probe already found, or (when typed as a new name) created for real via CreateTableAsync.
  readonly extraTables = input<string[]>([]);
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
  /** A table created via "Create a new table…" was given a parent/FK relationship — the parent wizard
   *  owns this globally (it outlives any one resource's canvas instance) so it survives navigating
   *  between resources, node reload, and the Mapping JSON export/import. */
  readonly childTableRelationAdded = output<{ tableName: string; relation: ChildTableRelation }>();
  /** A schema-authoring action (create table / add column / drop column / alter column) was triggered —
   *  queued for the wizard to actually execute (via a real DDL call) only once "Add to Pipeline" is
   *  clicked, instead of hitting the live database immediately. See PendingSchemaOp's own doc comment. */
  readonly schemaOpQueued = output<PendingSchemaOp>();

  // ── local UI state ──────────────────────────────────────────────────────
  readonly collapsedIds = signal<Set<string>>(new Set());
  readonly armedId = signal<string | null>(null);
  private armedKind: 'group' | 'leaf' | null = null;
  readonly dragTempWire = signal<FmTempWire | null>(null);
  private dragSourceId: string | null = null;
  private dragSourceKind: 'group' | 'leaf' | null = null;
  readonly popoverKey = signal<{ resource: string; tableName: string; targetName: string } | null>(null);
  readonly drawerOpen = signal(false);
  /** Free-text column names typed via "+ Add column" that don't have a mapping yet (CSV / un-probed SQL only), keyed by "resource::tableName". */
  private readonly pendingFreeColumns = signal<Record<string, string[]>>({});

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
    return !this.hasSqlTables() || this.sqlTableOptions().includes(this.targetFor(resource));
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
    const columnsForResource = (r: string): string[] => this.columnsForCardFn(r, this.targetFor(r), false);
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

  /** Column list for (resource, tableName) — used by the mapping-list draft form. */
  columnsForResourceTableFn = (resource: string, tableName: string): string[] => {
    const card = this.targetCards().find(c => c.resource === resource && c.tableName === tableName);
    return this.columnsForCardFn(resource, tableName, card?.isExtra ?? false);
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

  // ── viewport pan / zoom (mirrors the main workflow canvas's CanvasService interaction pattern:
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
   *  field/column list (.fm-source-rows/.fm-target-rows) — but neither ever actually grows a real
   *  scrollbar of its own (both cards are deliberately auto-height/uncapped, see
   *  field-mapping-source-tree.component.scss's own "this never actually clips/scrolls" note), so that
   *  exemption just silently ate the wheel event over a long list (e.g. a 70+-field resource) with
   *  nothing picking it up — the one place in the canvas a user would most naturally try to scroll. Still
   *  deferred for .fm-add-table-options, which is a real, genuinely-scrollable dropdown panel. */
  onViewportWheel(ev: WheelEvent): void {
    if (ev.ctrlKey || ev.metaKey) {
      ev.preventDefault();
      this.anchors.setZoom(this.anchors.zoom() * (ev.deltaY < 0 ? 1.1 : 0.9), ev.clientX, ev.clientY);
      return;
    }
    if ((ev.target as HTMLElement).closest('.fm-add-table-options')) {
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
    this.pendingFreeColumns.update(m => ({ ...m, [key]: [...(m[key] ?? []), column] }));
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
    this.pendingFreeColumns.update(m => ({
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

    const triggerRect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const viewportRect = this.viewport().nativeElement.getBoundingClientRect();
    const margin = 8;
    const spaceBelow = viewportRect.bottom - triggerRect.bottom - margin;
    const spaceAbove = triggerRect.top - viewportRect.top - margin;
    const minUsableHeight = 120;

    this.addTableMenuStyle.set(
      spaceBelow >= minUsableHeight || spaceBelow >= spaceAbove
        ? { top: triggerRect.bottom + 6, left: triggerRect.left, width: triggerRect.width, maxHeight: Math.max(minUsableHeight, spaceBelow) }
        : { bottom: window.innerHeight - triggerRect.top + 6, left: triggerRect.left, width: triggerRect.width, maxHeight: Math.max(minUsableHeight, spaceAbove) }
    );
    this.addTableSearchQuery.set('');
    this.addTableMenuOpen.set(true);
    // One tick so the panel (and its search input, an @if-conditional sibling of this trigger) has
    // actually rendered before we try to focus it.
    setTimeout(() => this.addTableSearchInput()?.nativeElement.focus());
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
    this.addTableSearchInput()?.nativeElement.focus();
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
    this.toast.success('Table added', `${name} is ready to map.`);
  }

  closeCreateTableModal(): void {
    if (this.creatingTableSubmitting()) return;
    this.creatingNewTable.set(false);
    this.creatingTableError.set(null);
    this.creatingTableResource = null;
  }

  /**
   * Submits the create-table modal — no database call happens here. The real CREATE TABLE only runs
   * once "Add to Pipeline" flushes the queue (see schemaOpQueued/DestinationWizardComponent), so the
   * table shown here is a locally-synthesized preview (mirroring SqlDestinationSchemaService.
   * CreateTableAsync's own shape/defaults) rather than the backend's authoritative response — the flush
   * overwrites it with the real one once it actually executes.
   */
  submitCreateTable(submission: FmCreateTableSubmit): void {
    const resource = this.creatingTableResource;
    const typed = submission.tableName.trim();
    if (!resource || !typed) return;
    // Mirrors SqlDestinationSchemaService.SplitTableName's "dbo" default so extraTables()/sqlTables()
    // agree on the same key everywhere, or the new table's card resolves zero columns via
    // columnsForTable() even though the create appears to have "succeeded" (columns silently invisible).
    const name = typed.includes('.') ? typed : `dbo.${typed}`;
    // targetFor(resource) may just be an unfulfilled guess (no card shown for it, see isPrimaryTargetValid)
    // — that's exactly the name this create is most likely trying to fulfill, not a real dupe.
    const targetAlreadyReal = this.isPrimaryTargetValid(resource) && this.targetFor(resource) === name;
    if (this.extraTables().includes(name) || targetAlreadyReal) {
      this.creatingTableError.set(`${name} is already on this canvas.`);
      return;
    }
    const connection = this.connectionInfo();
    if (!connection) return;

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

    const dot = name.indexOf('.');
    const table: DestinationTable = {
      schemaName: dot >= 0 ? name.slice(0, dot) : 'dbo',
      tableName: dot >= 0 ? name.slice(dot + 1) : name,
      fullName: name,
      origin: 'userCreated',
      columns: [
        { name: 'Id', dataType: 'bigint', mappingValueType: 'Integer', isNullable: false, maxLength: null, isPrimaryKey: true, origin: 'userCreated' },
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
    if (this.destType() === 'sql' && this.hasSqlTables()) {
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
    this.pendingFreeColumns.update(m => ({ ...m, [key]: (m[key] ?? []).filter(c => c !== column) }));
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

    const column: DestinationColumn = {
      name: submission.columnName,
      dataType: submission.dataType,
      mappingValueType: this.mapSqlServerType(submission.dataType),
      isNullable: true,
      maxLength: null,
      origin: 'userCreated',
    };
    // Mirrors AddColumnAsync's own "auto-create the table if it doesn't exist yet" behavior — if this
    // target table isn't already known, the real call will bring it into existence too, so the local
    // preview needs to register a bare (Id + this column) table, not just append to one that isn't there.
    const tableAlreadyKnown = this.sqlTableOptions().includes(target.tableName);
    let table: DestinationTable | undefined;
    if (!tableAlreadyKnown) {
      const dot = target.tableName.indexOf('.');
      table = {
        schemaName: dot >= 0 ? target.tableName.slice(0, dot) : 'dbo',
        tableName: dot >= 0 ? target.tableName.slice(dot + 1) : target.tableName,
        fullName: target.tableName,
        origin: 'userCreated',
        columns: [
          { name: 'Id', dataType: 'bigint', mappingValueType: 'Integer', isNullable: false, maxLength: null, isPrimaryKey: true, origin: 'userCreated' },
          column,
        ],
      };
    }

    this.columnAdded.emit({ tableName: target.tableName, column, table });
    if (!this.hasSqlTables()) this.registerPendingColumn(target.resource, target.tableName, submission.columnName);
    this.schemaOpQueued.emit({
      kind: 'addColumn',
      request: { connection, tableName: target.tableName, columnName: submission.columnName, dataType: submission.dataType },
    });
    this.toast.success('Column queued', `${submission.columnName} will be added to ${target.tableName} when you click "Add to Pipeline".`);
    this.addColumnTarget.set(null);
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

  submitLoadPayload(raw: string): void {
    const resource = this.resources()[0];
    if (!resource) return;

    const result = parseSourcePayloadJson(resource, raw);
    if (!result.ok) {
      this.loadPayloadError.set(result.error);
      return;
    }

    this.sourcePayloadLoaded.emit({ resource, fields: result.fields });
    this.loadPayloadOpen.set(false);
    this.loadPayloadError.set(null);
    const count = `${result.fields.length} field${result.fields.length === 1 ? '' : 's'}`;
    this.toast.success(
      'Payload loaded',
      result.declaredResourceType
        ? `${count} found (pasted JSON declares resourceType "${result.declaredResourceType}").`
        : `${count} found in the pasted ${resource} JSON.`,
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
  private completeMapping(sourceId: string, kind: 'group' | 'leaf', resource: string, tableName: string, column: string): void {
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

  updateRow(updated: MappingRow): void {
    this.mappingRowsChange.emit(
      this.mappingRows().map(r =>
        (r.resource === updated.resource && r.tableName === updated.tableName && r.targetName === updated.targetName) ? updated : r,
      ),
    );
  }

  /** Only one row per resource can be the upsert key (the backend resolves a single key column — see
   *  MappedSqlServerDestinationWriter.ResolveUpsertKeyColumn) — so marking one on clears any other
   *  explicit key already set for the same resource. Marking the already-active row off drops the
   *  explicit override entirely, reverting that resource to the real-PK fallback in serializeRowsFlat. */
  onToggleUpsertKey(resource: string, tableName: string, column: string): void {
    const target = this.rowForColumnFn(resource, tableName, column);
    if (!target) return;
    const turningOn = !target.isUpsertKey;
    this.mappingRowsChange.emit(
      this.mappingRows().map(r => {
        if (r.resource !== resource) return r;
        if (r === target) return { ...r, isUpsertKey: turningOn };
        return r.isUpsertKey ? { ...r, isUpsertKey: false } : r;
      }),
    );
  }

  // ── popover / drawer ─────────────────────────────────────────────────────
  popoverRow = computed<MappingRow | null>(() => {
    const key = this.popoverKey();
    if (!key) return null;
    return this.rowForColumnFn(key.resource, key.tableName, key.targetName) ?? null;
  });

  onWireClick(e: { resource: string; tableName: string; targetName: string }): void {
    if (this.rowForColumnFn(e.resource, e.tableName, e.targetName)) this.popoverKey.set(e);
  }

  onEditRow(e: { resource: string; tableName: string; targetName: string; invoker: HTMLElement }): void {
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

  closePopover(): void { this.popoverKey.set(null); }

  onPopoverSave(row: MappingRow): void {
    this.updateRow(row);
    this.closePopover();
  }

  onPopoverRemove(): void {
    const key = this.popoverKey();
    if (key) this.removeRow(key.resource, key.tableName, key.targetName);
    this.closePopover();
  }

  openDrawer(): void { this.drawerOpen.set(true); }
  closeDrawer(): void { this.drawerOpen.set(false); }
}
