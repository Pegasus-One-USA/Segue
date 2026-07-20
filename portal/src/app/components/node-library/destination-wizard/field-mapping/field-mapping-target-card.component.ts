import {
  Component, ElementRef, inject, input, output, viewChild, viewChildren, AfterViewInit, OnDestroy,
} from '@angular/core';
import { MappingRow } from './field-mapping-model';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

/**
 * One destination card per resource — the table/file-name selector (unchanged from the original
 * step-3 markup) plus one row per existing destination column. Columns come from the live SQL schema
 * probe when available, or render as an editable free-text row (CSV, or SQL not yet probed) — exactly
 * today's fallback behavior, just restyled. "+ Add column" opens a real ALTER TABLE modal for any SQL
 * card — connected or not, matching "+ Add a table"'s same not-gated-behind-a-probe behavior; CSV has
 * no real schema to alter, so it keeps the free-text/local-only row.
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
  readonly destType = input.required<'sql' | 'csv'>();
  readonly targetValue = input.required<string>();
  readonly hasSqlTables = input.required<boolean>();
  readonly sqlTableOptions = input.required<string[]>();
  readonly columns = input.required<string[]>();
  readonly rowForColumn = input.required<(column: string) => MappingRow | undefined>();
  readonly isArmed = input.required<boolean>();
  readonly isApproximated = input.required<(row: MappingRow) => boolean>();
  readonly x = input.required<number>();
  readonly y = input.required<number>();

  readonly targetChange = output<string>();
  readonly addFreeColumn = output<string>();
  readonly openAddColumn = output<void>();
  readonly columnActivate = output<string>();
  readonly positionChange = output<{ x: number; y: number }>();
  readonly removeTable = output<void>();
  readonly deleteColumn = output<string>();

  private dragOffset: { dx: number; dy: number } | null = null;

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

  private registerRows(): void {
    this.columnRows().forEach((ref, i) => {
      const col = this.columns()[i];
      if (col) this.anchors.register(`${this.resource()}::${this.tableName()}::${col}`, ref.nativeElement);
    });
  }

  targetLabel(): string { return this.destType() === 'sql' ? 'Table' : 'File name'; }

  onTargetInput(value: string): void { this.targetChange.emit(value); }

  onNewColumnKeydown(ev: KeyboardEvent): void {
    if (ev.key !== 'Enter') return;
    const input = ev.target as HTMLInputElement;
    const name = input.value.trim();
    if (name) { this.addFreeColumn.emit(name); input.value = ''; }
  }

  onColumnKeydown(column: string, ev: KeyboardEvent): void {
    if (ev.key !== 'Enter' && ev.key !== ' ') return;
    // Let the delete button handle its own Enter/Space activation rather than also completing a mapping.
    if ((ev.target as HTMLElement).closest('button')) return;
    ev.preventDefault();
    this.columnActivate.emit(column);
  }
}
