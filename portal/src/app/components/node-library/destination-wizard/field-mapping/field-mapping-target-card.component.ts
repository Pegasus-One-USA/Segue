import {
  Component, ElementRef, computed, effect, inject, input, output, signal, viewChild, viewChildren, AfterViewInit, OnDestroy,
} from '@angular/core';
import { MappingRow, MappingDestType, isSqlFamilyDestType } from './field-mapping-model';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { ChildTableRelation, detectRelationFromColumns } from './field-mapping-summary.model';
import { autoCardWidth } from './field-mapping-card-size.util';
import { searchTerms, matchesSearchTerms } from './field-mapping-tree.util';
import { tableTargetStatus, TableTargetStatus } from './field-mapping-schema-ops.util';

const MIN_WIDTH = 220;
const MAX_WIDTH = 640;
// Higher base than the source card's — every row here also carries a port dot, an optional type badge,
// an optional PK/FK badge + key-toggle, and edit/delete buttons, none of which the source tree has.
const BASE_PADDING_PX = 170;

/** Just the slice of DestinationColumn this card needs — a PK/FK badge, plus origin to decide whether
 *  edit/delete are offered at all (see canEditColumn). */
export interface FmColumnKeyInfo {
  isPrimaryKey?: boolean;
  isForeignKey?: boolean;
  references?: string | null;
  /** Absent (e.g. every real probe() response) means an already-existing column on a real, live
   *  database table — never edited/deleted from here, since that would be a silent, un-reviewed schema
   *  change against a table other things may already depend on. 'userCreated' means it was added THIS
   *  session, on this canvas (a brand-new table's own columns, or "+ Add column" on an existing table) —
   *  nothing has been built against it outside this canvas yet, so it's still safe to edit/delete. */
  origin?: 'probed' | 'userCreated' | 'restoredUnverified';
}

/**
 * One destination card per resource — one row per existing destination column; which table/file this
 * card represents is chosen entirely via the canvas-level "+ Add a table…" control (see
 * FieldMappingCanvasComponent.openCreateTableModal/onAddExtraTable), not by anything on the card itself.
 * Columns come from the live SQL schema probe when available, or render as an editable free-text row
 * (CSV, or SQL not yet probed) — exactly today's fallback behavior, just restyled. "+ Add column" opens
 * a real ALTER TABLE modal for any SQL card — connected or not, matching "+ Add a table"'s same
 * not-gated-behind-a-probe behavior; CSV has no real schema to alter, so it keeps the free-text/local-only row.
 */
@Component({
  selector: 'app-field-mapping-target-card',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-target-card.component.html',
  styleUrl: './field-mapping-target-card.component.scss',
})
export class FieldMappingTargetCardComponent implements AfterViewInit, OnDestroy {
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly columnRows = viewChildren<ElementRef<HTMLElement>>('columnRow');
  private readonly card = viewChild.required<ElementRef<HTMLElement>>('card');
  private resizeObserver: ResizeObserver | null = null;

  readonly resource = input.required<string>();
  /** Which destination table this card represents — the resource's primary table, or an added extra one. */
  readonly tableName = input.required<string>();
  /** True for an added extra (already-existing) table — shows a remove button, hides the table picker. */
  readonly isExtra = input<boolean>(false);
  readonly destType = input.required<MappingDestType>();
  readonly targetValue = input.required<string>();
  readonly hasSqlTables = input.required<boolean>();
  readonly sqlTableOptions = input.required<string[]>();
  /** Table full-names with a "Create a new table…"/"Add column" op queued this session but not yet
   *  flushed — see PendingSchemaOp and DestinationWizardComponent.pendingTableNames. Drives tableStatus's
   *  'pending' badge; defaults to empty for any host that doesn't (yet) thread it through. */
  readonly pendingTableNames = input<ReadonlySet<string>>(new Set());
  readonly columns = input.required<string[]>();
  readonly rowForColumn = input.required<(column: string) => MappingRow | undefined>();
  /** Real data type (e.g. "nvarchar(50)") for a probed/created SQL column — undefined for CSV or
   *  free-text columns that have no real schema behind them, in which case no type badge is shown. */
  readonly columnDataType = input<(column: string) => string | undefined>(() => undefined);
  /** Real PK/FK status for a probed/created SQL column (see DestinationColumn.isPrimaryKey/isForeignKey/
   *  references) — undefined for CSV or free-text columns that have no real schema behind them, in which
   *  case no key badge is shown. */
  readonly columnKeyInfo = input<(column: string) => FmColumnKeyInfo | undefined>(() => undefined);
  /** Whether a column is the EFFECTIVE upsert key (user's explicit override for this resource if one
   *  exists, else the destination table's real PK) — drives the key-toggle button's pressed state. */
  readonly isUpsertKeyColumn = input<(column: string) => boolean>(() => false);
  /** Set only when this table was created as a child of another (see ChildTableRelation) — read-only
   *  display of an already-known relation, same display-only role as columnDataType/columnKeyInfo. */
  readonly relation = input<ChildTableRelation | undefined>(undefined);
  readonly isArmed = input.required<boolean>();
  readonly isApproximated = input.required<(row: MappingRow) => boolean>();
  /** See FieldMappingCanvasComponent.schemaAuthoringEnabled's doc comment — false hides the real
   *  "+ Add column" trigger for a probed SQL table (no live credentials to back the ALTER TABLE it
   *  would open). Defaults true so the Destination Wizard is unaffected. */
  readonly schemaAuthoringEnabled = input(true);
  readonly x = input.required<number>();
  readonly y = input.required<number>();
  /** Toolbar-level search (see NodeLibraryDialogComponent's toolbar search box, forwarded through
   *  FieldMappingCanvasComponent) — ANDed together with this card's own searchQuery below to actually
   *  drive filtering (see effectiveQuery), but deliberately never written INTO searchQuery: typing in
   *  the toolbar must filter these columns without visibly changing what's shown in this card's own box
   *  (and vice versa — this card's own typing never echoes back up to the toolbar either). */
  readonly externalQuery = input<string>('');

  readonly addFreeColumn = output<string>();
  readonly openAddColumn = output<void>();
  readonly columnActivate = output<string>();
  readonly positionChange = output<{ x: number; y: number }>();
  readonly removeTable = output<void>();
  readonly deleteColumn = output<string>();
  readonly editColumn = output<string>();
  /** A free-text column's name was changed via the inline rename (✎ on a column with no real schema —
   *  see isFreeTextColumn) — no real ALTER COLUMN here, unlike editColumn (a real, session-created SQL
   *  column, which still opens the real edit-column modal), so this is just "now called something else"
   *  for the parent to reflect locally. */
  readonly renameColumn = output<{ oldName: string; newName: string }>();
  /** A mapped column's key-toggle button was clicked — the parent decides whether that marks it as this
   *  resource's upsert key or clears it (see FieldMappingCanvasComponent.onToggleUpsertKey). */
  readonly toggleUpsertKey = output<string>();

  /** Which column's name is currently being edited inline (✎ clicked on a free-text column) — null when
   *  none is. */
  readonly renamingColumn = signal<string | null>(null);
  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');

  private dragOffset: { dx: number; dy: number } | null = null;

  constructor() {
    // Focus + select the rename input's text the moment it renders, so the user can start typing (or
    // hit Enter to keep the current name) without an extra click.
    effect(() => {
      const el = this.renameInput()?.nativeElement;
      if (el) { el.focus(); el.select(); }
    });
  }

  /**
   * False when this is the primary card, a live schema is known, and the current target (often just a
   * generic "dbo.{resource}" guess seeded before any real table was ever chosen) doesn't match any real
   * probed/created table. A native <select> can't actually display a value that matches none of its
   * <option>s — it silently falls back to showing its first real option instead, which looks exactly
   * like the user picked that table when they never did. Driving the placeholder option's selected state
   * off this (rather than off "is targetValue empty") keeps the dropdown honest, and gates "+ Add column"
   * so a column can't be added against a table nobody actually chose.
   */
  readonly hasValidTarget = computed(() =>
    this.isExtra() || !this.hasSqlTables() || this.sqlTableOptions().includes(this.targetValue())
  );

  /** SQL Server/MySQL/PostgreSQL all share this card's "real table" chrome (table kind label, the
   *  Suggested/Pending/Confirmed status badge, and the real "+ Add column" ALTER TABLE flow) — CSV/Mongo/
   *  Blob don't. See isSqlFamilyDestType's doc comment for why this can't just be `destType() === 'sql'`. */
  readonly isSqlFamily = computed(() => isSqlFamilyDestType(this.destType()));

  /** Pending / Confirmed — see TableTargetStatus's own doc comment for what each means. Driven by the
   *  exact same pendingTableNames the canvas itself uses to gate Create-Table/Add-Column, so this badge
   *  can never disagree with what those gates actually allow — for the primary table AND an extra table
   *  alike. isExtra() used to force 'confirmed' unconditionally here on the theory that an extra table
   *  only ever gets added via "+ Add a table from your database…" (onAddExtraTable), which does only
   *  ever offer already-probed, real names — but extraTablesByGroup can also be populated straight from a
   *  restored dest_mapping_summary_v1 with no live check at all (applyMappingSummaryDocument), so a
   *  restored extra table whose table has since been dropped could reach this card too. Neither this card
   *  nor tableStatus needs to know the difference any more: FieldMappingCanvasComponent.targetCards()
   *  now applies the same "confirmed or pending" gate to extras that isPrimaryTargetValid already applied
   *  to the primary target (see isExtraTableValid there), so this card is never rendered at all for a
   *  target — primary or extra — that's neither. There's nothing left for this computed to decide beyond
   *  "which of the two", for both cases identically. */
  readonly tableStatus = computed<TableTargetStatus>(() =>
    tableTargetStatus(this.tableName(), this.pendingTableNames())
  );

  /**
   * Derives the same "child of X via Y" relation straight from the real per-column FK metadata
   * (columnKeyInfo/DestinationColumn.references) — unlike the `relation` input (only ever populated for
   * a table created THIS session via "Create a new table…"), this also covers an already-existing table
   * picked from the database dropdown that just happens to have a real FK constraint. Whichever column
   * actually has isForeignKey wins; a table normally has at most one FK back to its logical parent.
   */
  readonly detectedRelation = computed<ChildTableRelation | undefined>(() =>
    detectRelationFromColumns(this.columns().map(name => ({ name, ...this.columnKeyInfo()(name) }))),
  );

  /** DB-detected relation wins when known; falls back to the explicit `relation` input for a table whose
   *  columns aren't live yet (e.g. right after creation, before a re-probe, or CSV with no real schema). */
  readonly relationToShow = computed(() => this.detectedRelation() ?? this.relation());

  // ── search ────────────────────────────────────────────────────────────────
  readonly searchQuery = signal('');
  /** Drives ONLY this card's own inline clear (✕) button — local text alone, so that button never
   *  appears "clearable" over a filter that's actually coming from the toolbar (nothing here to clear). */
  readonly isSearching = computed(() => this.searchQuery().trim().length > 0);

  /** What actually drives filtering: the toolbar query AND this card's own box, combined as extra AND
   *  terms — so the toolbar narrows every table's columns at once and this card's own box can further
   *  refine within that, without either overwriting the other's displayed text (see externalQuery). */
  readonly effectiveQuery = computed(() => `${this.externalQuery()} ${this.searchQuery()}`.trim());
  /** True whenever a filter is actually in effect, from either source — unlike isSearching (local-only,
   *  for the clear button), this gates the "no columns match" empty state. */
  readonly hasActiveFilter = computed(() => this.effectiveQuery().length > 0);

  /** Matches by column name AND by its real data type (columnDataType — e.g. "bigint", "int",
   *  "nvarchar(50)" for a probed/created SQL column; undefined for a free-text/CSV column, which then
   *  only matches by name) — same multi-term AND/OR semantics as the payload tree's own search (see
   *  matchesSearchTerms), so "int" finds every integer column and "patient int" combines a name/path
   *  term with a type term exactly like the source side does. */
  readonly filteredColumns = computed(() => {
    const terms = searchTerms(this.effectiveQuery());
    if (!terms.length) return this.columns();
    const typeOf = this.columnDataType();
    return this.columns().filter(c => matchesSearchTerms([c, typeOf(c) ?? ''], terms));
  });

  onSearchInput(value: string): void { this.searchQuery.set(value); }
  clearSearch(): void { this.searchQuery.set(''); }

  /** Native scroll inside .fm-target-rows doesn't touch anchors.version/pan/zoom on its own — without
   *  this, a wire to/from a row scrolled into or out of view (see FieldMappingAnchorService's
   *  clampToScrollableAncestor) would stay frozen at its pre-scroll position until some unrelated change
   *  happened to bump the anchor registry. */
  onRowsScroll(): void { this.anchors.refreshAll(); }

  // ── fit-to-screen / expand ───────────────────────────────────────────────
  // Defaults to "fit" (capped, scrollable) rather than the old always-uncapped behavior — a table with
  // many columns no longer forces panning the whole canvas just to reach one near the bottom. Backed by
  // a CSS max-height cap (.fm-target-card--fit), not a direct height binding: a continuously-bound
  // height would fight the native drag-resize handle (which sets inline height directly) on every
  // change-detection cycle. max-height only ever narrows the visible box on top of whatever height is
  // currently set, so it never needs to touch — or undo — the user's own drag.
  readonly fitMode = signal(true);

  toggleFit(): void {
    const nowFit = !this.fitMode();
    this.fitMode.set(nowFit);
    if (!nowFit) {
      // Expanding: clear any inline height left over from a drag-resize while fitted, so "expand" really
      // does mean "show everything", matching the card's natural auto-height default.
      this.card().nativeElement.style.height = '';
    }
  }

  onHeadPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    // Don't start a drag from the remove ("✕") button — setPointerCapture on the head would otherwise
    // redirect the subsequent pointerup (and the click derived from it) away from the button, silently
    // swallowing the click before removeTable ever fires.
    if ((ev.target as HTMLElement).closest('button')) return;
    const el = ev.currentTarget as HTMLElement;
    el.setPointerCapture(ev.pointerId);
    const p = this.anchors.toViewportPoint(ev.clientX, ev.clientY);
    this.dragOffset = { dx: p.x - this.x(), dy: p.y - this.y() };
  }

  onHeadPointerMove(ev: PointerEvent): void {
    if (!this.dragOffset) return;
    const p = this.anchors.toViewportPoint(ev.clientX, ev.clientY);
    this.positionChange.emit({ x: p.x - this.dragOffset.dx, y: p.y - this.dragOffset.dy });
  }

  onHeadPointerUp(ev: PointerEvent): void {
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.dragOffset = null;
  }

  ngAfterViewInit(): void {
    this.registerRows();
    // One-time default sized to fit the longest column/target name on this table, in place of the old
    // flat 300px default — never re-applied afterward (see field-mapping-card-size.util.ts), so it can't
    // fight the user's own drag-resize later.
    const longest = Math.max(this.targetValue().length, ...this.columns().map(c => c.length), 0);
    this.card().nativeElement.style.width = `${autoCardWidth([longest], BASE_PADDING_PX, MIN_WIDTH, MAX_WIDTH)}px`;

    // The card is now user-resizable (CSS `resize: both`), which doesn't fire any DOM event or trigger
    // Angular change detection on its own — without this, wires attached to rows inside it would
    // visually lag behind a drag-resize until some unrelated action happened to run ngDoCheck.
    this.resizeObserver = new ResizeObserver(() => this.anchors.refreshAll());
    this.resizeObserver.observe(this.card().nativeElement);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
  }

  ngDoCheck(): void {
    // Column set changes as often as the user edits free-text names; re-register is cheap (Map.set).
    this.registerRows();
  }

  // Tracks which columns are CURRENTLY registered so a column hidden by an active search filter (rather
  // than actually deleted) gets its anchor unregistered too — otherwise a mapped column's wire would
  // keep pointing at a detached DOM node (getBoundingClientRect() on it returns an all-zero rect).
  private readonly registeredColumns = new Set<string>();

  private registerRows(): void {
    const visible = this.filteredColumns();
    const visibleSet = new Set(visible);
    for (const col of this.registeredColumns) {
      if (!visibleSet.has(col)) {
        this.anchors.unregister(`${this.resource()}::${this.tableName()}::${col}`);
        this.registeredColumns.delete(col);
      }
    }
    this.columnRows().forEach((ref, i) => {
      const col = visible[i];
      if (!col) return;
      this.anchors.register(`${this.resource()}::${this.tableName()}::${col}`, ref.nativeElement);
      this.registeredColumns.add(col);
    });
  }

  /** Edit/delete are only offered for a column this canvas actually created — a real pre-existing column
   *  probed from the live database is display-only here. No real column metadata at all (CSV, or a SQL
   *  table before any probe succeeds) means there's no live schema to protect either way, so those
   *  free-text rows stay fully editable, same as always. */
  canEditColumn(col: string): boolean {
    const info = this.columnKeyInfo()(col);
    return !info || info.origin === 'userCreated';
  }

  /** True when a column has no real schema behind it at all (CSV, or a SQL table before any probe
   *  succeeds) — its ✎ does a simple inline rename instead of opening the real ALTER-COLUMN modal
   *  (which needs a real data type to edit alongside the name). A session-created SQL column (userCreated)
   *  still has a real data type worth editing, so it keeps the modal. */
  isFreeTextColumn(col: string): boolean {
    return !this.columnKeyInfo()(col);
  }

  /** "dbo.Patient" -> "Patient" — the relation banner reads better without the repeated schema prefix. */
  relationParentLabel(): string {
    const parent = this.relationToShow()?.parentTable ?? '';
    return parent.includes('.') ? parent.slice(parent.lastIndexOf('.') + 1) : parent;
  }

  onNewColumnKeydown(ev: KeyboardEvent): void {
    if (ev.key !== 'Enter') return;
    this.commitNewColumn(ev.target as HTMLInputElement);
  }

  /** Also commits on blur (clicking away), not just Enter — typing a name and then clicking elsewhere
   *  should still add it rather than silently losing it. */
  onNewColumnBlur(ev: FocusEvent): void {
    this.commitNewColumn(ev.target as HTMLInputElement);
  }

  commitNewColumn(input: HTMLInputElement): void {
    const name = input.value.trim();
    if (name) { this.addFreeColumn.emit(name); input.value = ''; }
  }

  startRename(column: string): void {
    this.renamingColumn.set(column);
  }

  onRenameKeydown(oldName: string, ev: KeyboardEvent): void {
    if (ev.key === 'Escape') { this.renamingColumn.set(null); return; }
    if (ev.key !== 'Enter') return;
    this.commitRename(oldName, ev.target as HTMLInputElement);
  }

  onRenameBlur(oldName: string, ev: FocusEvent): void {
    this.commitRename(oldName, ev.target as HTMLInputElement);
  }

  private commitRename(oldName: string, input: HTMLInputElement): void {
    const newName = input.value.trim();
    this.renamingColumn.set(null);
    if (newName && newName !== oldName) this.renameColumn.emit({ oldName, newName });
  }

  onColumnKeydown(column: string, ev: KeyboardEvent): void {
    if (ev.key !== 'Enter' && ev.key !== ' ') return;
    // Let the delete button handle its own Enter/Space activation rather than also completing a mapping.
    if ((ev.target as HTMLElement).closest('button')) return;
    ev.preventDefault();
    this.columnActivate.emit(column);
  }
}
