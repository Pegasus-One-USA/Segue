import {
  Component, ElementRef, computed, effect, inject, input, output, signal, viewChild, viewChildren, AfterViewInit, OnDestroy,
} from '@angular/core';
import { MappingRow } from './field-mapping-model';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { ChildTableRelation } from './field-mapping-summary.model';
import { autoCardWidth } from './field-mapping-card-size.util';

const MIN_WIDTH = 220;
const MAX_WIDTH = 640;
// Higher base than the source card's — every row here also carries a port dot and edit/delete buttons,
// none of which the source tree has.
const BASE_PADDING_PX = 170;

/** Just the PK/FK-relevant slice of DestinationColumn — kept for shape-parity with the SQL-family
 *  variants, even though Mongo never actually has a live schema to populate it from. */
export interface FmColumnKeyInfo {
  isPrimaryKey?: boolean;
  isForeignKey?: boolean;
  references?: string | null;
}

/**
 * MongoDB variant of the destination card — one per resource's collection, plus one row per mapped
 * field. Unlike the SQL-family variants (field-mapping-target-card-sql-server/-mysql/-postgres), Mongo
 * has no live schema to probe or ALTER — the collection name and every field row are always plain
 * free-text, so this component skips the dropdown/"+ Add column real button" machinery entirely rather
 * than carrying dead `hasSqlTables`-gated branches that can never actually fire for this type. See
 * field-mapping-target-card-csv for the other free-text variant, and
 * _field-mapping-target-card-shared.scss for the styling all five share.
 */
@Component({
  selector: 'app-field-mapping-target-card-mongo',
  standalone: true,
  imports: [],
  templateUrl: './field-mapping-target-card-mongo.component.html',
  styleUrl: './field-mapping-target-card-mongo.component.scss',
})
export class FieldMappingTargetCardMongoComponent implements AfterViewInit, OnDestroy {
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly columnRows = viewChildren<ElementRef<HTMLElement>>('columnRow');
  private readonly card = viewChild.required<ElementRef<HTMLElement>>('card');
  private resizeObserver: ResizeObserver | null = null;

  readonly resource = input.required<string>();
  /** Which destination collection this card represents — the resource's primary collection, or an
   *  added extra one. */
  readonly tableName = input.required<string>();
  /** True for an added extra (already-existing) collection — shows a remove button, hides the picker. */
  readonly isExtra = input<boolean>(false);
  readonly targetValue = input.required<string>();
  readonly columns = input.required<string[]>();
  readonly rowForColumn = input.required<(column: string) => MappingRow | undefined>();
  readonly columnDataType = input<(column: string) => string | undefined>(() => undefined);
  readonly columnKeyInfo = input<(column: string) => FmColumnKeyInfo | undefined>(() => undefined);
  readonly isUpsertKeyColumn = input<(column: string) => boolean>(() => false);
  /** Set only when this collection was created as a child of another (see ChildTableRelation) —
   *  read-only display of an already-known relation, same display-only role as columnDataType/columnKeyInfo. */
  readonly relation = input<ChildTableRelation | undefined>(undefined);
  readonly isArmed = input.required<boolean>();
  readonly isApproximated = input.required<(row: MappingRow) => boolean>();
  readonly x = input.required<number>();
  readonly y = input.required<number>();

  readonly targetChange = output<string>();
  readonly addFreeColumn = output<string>();
  readonly columnActivate = output<string>();
  readonly positionChange = output<{ x: number; y: number }>();
  readonly removeTable = output<void>();
  readonly deleteColumn = output<string>();
  /** A free-text field's name was changed via the inline rename (✎) — no real schema to ALTER here
   *  (unlike the SQL-family variants' editColumn, which opens a real ALTER COLUMN modal), so this is
   *  just "this field is now called something else" for the parent to reflect locally. */
  readonly renameColumn = output<{ oldName: string; newName: string }>();
  /** A mapped field's key-toggle button was clicked — the parent decides whether that marks it as this
   *  resource's upsert key or clears it (see FieldMappingCanvasComponent.onToggleUpsertKey). */
  readonly toggleUpsertKey = output<string>();

  /** Which field's name is currently being edited inline (✎ clicked) — null when none is. */
  readonly renamingColumn = signal<string | null>(null);
  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');

  constructor() {
    // Focus + select the rename input's text the moment it renders, so the user can start typing (or
    // hit Enter to keep the current name) without an extra click — same affordance a native inline-
    // rename control (e.g. a file explorer) would give for free.
    effect(() => {
      const el = this.renameInput()?.nativeElement;
      if (el) { el.focus(); el.select(); }
    });
  }

  private dragOffset: { dx: number; dy: number } | null = null;

  /** No live schema to detect a relation from (unlike the SQL-family variants) — always just the
   *  explicit `relation` input, only ever populated for a collection created THIS session. */
  readonly relationToShow = computed(() => this.relation());

  // ── search ────────────────────────────────────────────────────────────────
  readonly searchQuery = signal('');
  readonly isSearching = computed(() => this.searchQuery().trim().length > 0);
  readonly filteredColumns = computed(() => {
    const q = this.searchQuery().trim().toLowerCase();
    return q ? this.columns().filter(c => c.toLowerCase().includes(q)) : this.columns();
  });

  onSearchInput(value: string): void { this.searchQuery.set(value); }
  clearSearch(): void { this.searchQuery.set(''); }

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
    // One-time default sized to fit the longest column/target name on this card, in place of the old
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

  /** "acme.patients" -> "patients" — the relation banner reads better without a repeated database prefix. */
  relationParentLabel(): string {
    const parent = this.relationToShow()?.parentTable ?? '';
    return parent.includes('.') ? parent.slice(parent.lastIndexOf('.') + 1) : parent;
  }

  onTargetInput(value: string): void {
    this.targetChange.emit(value);
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

  private commitNewColumn(input: HTMLInputElement): void {
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
