import {
  Component, ElementRef, computed, effect, inject, input, output, signal, viewChild, AfterViewInit, OnDestroy,
} from '@angular/core';
import type { ResourceFieldDef } from '../destination-wizard.component';
import { MappingRow, MappingSourceRef, MappingInstanceSelection, isApproximated } from './field-mapping-model';
import { FmTreeNode, buildForest, findNode } from './field-mapping-tree.util';
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
import { ZoomDockComponent } from '../../../canvas/zoom-dock/zoom-dock.component';
import { ToastService } from '../../../../services/toast.service';
import { DestinationSchemaService, DestinationColumn, DestinationTable, DestinationProbeRequest } from '../../../../services/destination-schema.service';

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
    ZoomDockComponent,
  ],
  providers: [FieldMappingAnchorService],
  templateUrl: './field-mapping-canvas.component.html',
  styleUrl: './field-mapping-canvas.component.scss',
})
export class FieldMappingCanvasComponent implements AfterViewInit, OnDestroy {
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly toast = inject(ToastService);
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly canvasInner = viewChild.required<ElementRef<HTMLElement>>('canvasInner');
  private readonly viewport = viewChild.required<ElementRef<HTMLElement>>('viewport');
  private resizeObserver: ResizeObserver | null = null;
  private viewportResizeObserver: ResizeObserver | null = null;

  readonly resources = input.required<string[]>();
  readonly destType = input.required<'sql' | 'csv'>();
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
  readonly availableTablesToAdd = input<(resource: string) => string[]>(() => []);
  // Ad-hoc connection details (from the wizard's Step 1 SQL form) — powers the real ALTER TABLE /
  // CREATE TABLE calls below. Only meaningful for destType 'sql'.
  readonly connectionInfo = input<DestinationProbeRequest | null>(null);

  // Incrementing counters from the dialog header's "Load JSON payload"/"Preview output" buttons (moved
  // there so this canvas's own toolbar row can be dropped, giving the viewport back that height) — same
  // pattern as DestinationWizardComponent's exitMappingRequest/saveMappingRequest.
  readonly openLoadPayloadRequest = input<number>(0);
  readonly openPreviewRequest = input<number>(0);
  private _lastLoadPayloadTrigger = 0;
  private _lastPreviewTrigger = 0;

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

  // ── freely-draggable card positions ─────────────────────────────────────
  // Keyed by table full-name for target cards (unique per table), plus a reserved key for the source card.
  private static readonly SOURCE_KEY = '__source__';
  private readonly cardPositions = signal<Record<string, { x: number; y: number }>>({});

  private defaultPositionFor(key: string, index: number): { x: number; y: number } {
    if (key === FieldMappingCanvasComponent.SOURCE_KEY) return { x: 24, y: 24 };
    return { x: 420, y: 24 + index * 260 };
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

  /** All tables (primary + extras) a resource currently targets — used by the mapping-list draft form. */
  tablesForResourceFn = (resource: string): string[] =>
    this.targetCards().filter(c => c.resource === resource).map(c => c.tableName);

  /** Column list for (resource, tableName) — used by the mapping-list draft form. */
  columnsForResourceTableFn = (resource: string, tableName: string): string[] => {
    const card = this.targetCards().find(c => c.resource === resource && c.tableName === tableName);
    return this.columnsForCardFn(resource, tableName, card?.isExtra ?? false);
  };

  constructor() {
    // React only on an actual increment, never the initial read (both start at 0).
    effect(() => {
      const v = this.openLoadPayloadRequest();
      if (v !== this._lastLoadPayloadTrigger) { this._lastLoadPayloadTrigger = v; if (v > 0) this.openLoadPayloadModal(); }
    });
    effect(() => {
      const v = this.openPreviewRequest();
      if (v !== this._lastPreviewTrigger) { this._lastPreviewTrigger = v; if (v > 0) this.openDrawer(); }
    });
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
   *  both go through clampPanY/setScrollY. Zoom still works no matter what's under the cursor, but plain
   *  wheel defers entirely to a card's own field/column list when hovering one — preventDefault() here
   *  would otherwise cancel the browser's native scroll on .fm-source-rows/.fm-target-rows too (it
   *  cancels the whole wheel event's default action, not just "canvas pan"), leaving no way to actually
   *  scroll a long list without first moving the cursor off the card entirely. */
  onViewportWheel(ev: WheelEvent): void {
    if (ev.ctrlKey || ev.metaKey) {
      ev.preventDefault();
      this.anchors.setZoom(this.anchors.zoom() * (ev.deltaY < 0 ? 1.1 : 0.9), ev.clientX, ev.clientY);
      return;
    }
    if ((ev.target as HTMLElement).closest('.fm-source-rows, .fm-target-rows')) {
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

  // ── extra tables ("+ Add a table from your database…") ─────────────────
  // Whether the "Create a new table…" modal is currently open — toggled on by picking that option from
  // the dropdown, and back off once a create attempt resolves (or is cancelled).
  readonly creatingNewTable = signal(false);
  readonly createNewTableOption = '__create_new_table__';
  readonly creatingTableSubmitting = signal(false);
  readonly creatingTableError = signal<string | null>(null);
  private creatingTableResource: string | null = null;
  private creatingTableAsPrimary = false;

  /** Routes a dropdown selection: the special "create new" sentinel opens the create-table modal;
   *  anything else names an already-probed, already-existing table — no backend call needed. */
  onAddTableSelectChange(resource: string, value: string): void {
    if (value === this.createNewTableOption) {
      this.openCreateTableModal(resource);
      return;
    }
    this.onAddExtraTable(resource, value);
  }

  /** Also the direct entry point when no live schema exists at all (!hasSqlTables()) — there's no
   *  dropdown of existing tables to choose from in that case, so this is the only "add a table" option.
   *  asPrimary defaults to "whatever this resource actually needs right now": explicit true from the
   *  target-card's own "Table" select (it only offers "✎ Create a new table…" when its card is even
   *  showing, i.e. already valid — a deliberate swap); otherwise auto-detected from the canvas-level
   *  "+ Add a table…" control, which is the ONLY entry point while there's no primary card to have a
   *  select of its own yet. */
  openCreateTableModal(resource: string, asPrimary?: boolean): void {
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
   * Submits the create-table modal — always attempts a real CREATE TABLE against whatever connection
   * details are currently in the wizard's Step 1 form, not gated behind a prior successful
   * "Test connection". A clean "already exists" failure is treated as a hit (the table is real, just
   * not one we created) and still added as a reference. On success, the backend's returned table shape
   * (all columns, including the FK if this was a child table) is what actually gets synced — never a
   * locally-guessed one — since that's the only way to be sure the canvas reflects what's really there.
   */
  submitCreateTable(submission: FmCreateTableSubmit): void {
    const resource = this.creatingTableResource;
    const typed = submission.tableName.trim();
    if (!resource || !typed) return;
    // CreateTableAsync always returns a schema-qualified fullName (defaulting to "dbo" when the user
    // types a bare name — see SqlDestinationSchemaService.SplitTableName). extraTables()/sqlTables() must
    // agree on that same key everywhere, or the new table's card resolves zero columns via
    // columnsForTable() even though the table was created successfully (columns silently invisible).
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

    this.creatingTableSubmitting.set(true);
    this.creatingTableError.set(null);
    this.schemaSvc.createTable({
      connection,
      tableName: name,
      columns: submission.columns,
      parentTable: submission.parentTable,
      parentColumn: submission.parentColumn,
      foreignKeyColumnName: submission.foreignKeyColumnName,
    }).subscribe({
      next: result => {
        this.creatingTableSubmitting.set(false);
        if (result.success && result.table) {
          // Use the backend's own fullName (authoritative), not the locally-guessed one, as the key
          // added to extraTables()/targetByResource — they must be the exact same string for
          // columnsForTable()/columnsForResourceTarget() to resolve.
          this.tableCreated.emit(result.table);
          if (this.creatingTableAsPrimary) {
            this.onTargetChange(resource, result.table.fullName);
          } else {
            this.extraTablesChange.emit([...this.extraTables(), result.table.fullName]);
          }
          if (submission.parentTable) {
            // Server-side defaults (see CreateTableRequest docs): parentColumn defaults to "Id",
            // foreignKeyColumnName to "{parentTableName}Id" — replicated here since the response never
            // echoes the relationship back onto DestinationTable.
            const parentShortName = submission.parentTable.includes('.')
              ? submission.parentTable.split('.').pop()!
              : submission.parentTable;
            this.childTableRelationAdded.emit({
              tableName: result.table!.fullName,
              relation: {
                parentTable: submission.parentTable!,
                parentColumn: submission.parentColumn || 'Id',
                foreignKeyColumnName: submission.foreignKeyColumnName || `${parentShortName}Id`,
              },
            });
          }
          this.toast.success('Table created', `${result.table.fullName} was created and is ready to map.`);
          this.closeCreateTableModal();
          return;
        }
        if ((result.error ?? '').toLowerCase().includes('already exists')) {
          if (this.creatingTableAsPrimary) {
            this.onTargetChange(resource, name);
          } else {
            this.extraTablesChange.emit([...this.extraTables(), name]);
          }
          this.toast.info('Table added', `${name} already exists — added as a reference.`);
          this.closeCreateTableModal();
          return;
        }
        this.creatingTableError.set(result.error ?? 'Failed to create table.');
      },
      error: err => {
        this.creatingTableSubmitting.set(false);
        this.creatingTableError.set(err?.error?.error ?? err?.message ?? 'Failed to create table.');
      },
    });
  }

  // Removing a table drops any mappings already made onto it, so it's confirmed first rather than
  // acting immediately on click — matching the wizard's own confirm-before-discarding pattern.
  readonly pendingRemoveTable = signal<string | null>(null);

  onRemoveExtraTable(tableName: string): void {
    this.pendingRemoveTable.set(tableName);
  }

  cancelRemoveTable(): void {
    this.pendingRemoveTable.set(null);
  }

  confirmRemoveTable(): void {
    const tableName = this.pendingRemoveTable();
    if (!tableName) return;
    this.extraTablesChange.emit(this.extraTables().filter(t => t !== tableName));
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

    this.dropColumnSubmitting.set(true);
    this.schemaSvc.dropColumn({ connection, tableName: target.tableName, columnName: target.column }).subscribe({
      next: result => {
        this.dropColumnSubmitting.set(false);
        if (result.success) {
          this.columnDropped.emit({ tableName: target.tableName, column: target.column });
          this.removeRow(target.resource, target.tableName, target.column);
          this.toast.success('Column dropped', `${target.column} was permanently removed from ${target.tableName}.`);
          this.pendingDropColumn.set(null);
          return;
        }
        this.toast.error('Could not drop column', result.error ?? 'Unknown error.');
        this.pendingDropColumn.set(null);
      },
      error: err => {
        this.dropColumnSubmitting.set(false);
        this.toast.error('Could not drop column', err?.error?.error ?? err?.message ?? 'Unknown error.');
        this.pendingDropColumn.set(null);
      },
    });
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

    this.addColumnSubmitting.set(true);
    this.addColumnError.set(null);
    this.schemaSvc.addColumn({
      connection,
      tableName: target.tableName,
      columnName: submission.columnName,
      dataType: submission.dataType,
    }).subscribe({
      next: result => {
        this.addColumnSubmitting.set(false);
        if (result.success && result.column) {
          // hasSqlTables() true: the wizard's sqlTables() update (from this event) drives the card's
          // real-schema display. hasSqlTables() false: no live schema is consulted for display at all,
          // so track it locally the same way the free-text fallback already does.
          this.columnAdded.emit({ tableName: target.tableName, column: result.column, table: result.table ?? undefined });
          if (!this.hasSqlTables()) this.registerPendingColumn(target.resource, target.tableName, submission.columnName);
          this.toast.success('Column added', `${submission.columnName} added to ${target.tableName}.`);
          this.addColumnTarget.set(null);
          return;
        }
        this.addColumnError.set(result.error ?? 'Failed to add column.');
      },
      error: err => {
        this.addColumnSubmitting.set(false);
        this.addColumnError.set(err?.error?.error ?? err?.message ?? 'Failed to add column.');
      },
    });
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

    this.editColumnSubmitting.set(true);
    this.editColumnError.set(null);
    this.schemaSvc.alterColumn({
      connection,
      tableName: target.tableName,
      columnName: target.columnName,
      newColumnName: submission.newColumnName,
      newDataType: submission.newDataType,
    }).subscribe({
      next: result => {
        this.editColumnSubmitting.set(false);
        if (result.success && result.column) {
          this.columnAltered.emit({ tableName: target.tableName, oldColumnName: target.columnName, column: result.column });
          this.toast.success('Column updated', `${target.columnName} → ${result.column.name} on ${target.tableName}.`);
          this.editColumnTarget.set(null);
          return;
        }
        this.editColumnError.set(result.error ?? 'Failed to update column.');
      },
      error: err => {
        this.editColumnSubmitting.set(false);
        this.editColumnError.set(err?.error?.error ?? err?.message ?? 'Failed to update column.');
      },
    });
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
    this.completeMapping(sourceId, kind, parts[0], parts[1], parts[2]);
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
        resource, sources: [source], mode: 'value', instance: { type: 'all' },
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
      resource, sources: [source], mode: 'value', instance: { type: 'all' },
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
