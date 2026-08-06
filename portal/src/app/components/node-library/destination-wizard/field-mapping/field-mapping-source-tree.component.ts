import { AfterViewInit, Component, ElementRef, OnDestroy, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { FmTreeNode, filterForest, flattenLeaves } from './field-mapping-tree.util';
import { FieldMappingTreeNodeComponent, FmDragStart, FmDragMove, FmDragEnd } from './field-mapping-tree-node.component';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { autoCardWidth } from './field-mapping-card-size.util';

const MIN_WIDTH = 260;
const MAX_WIDTH = 720;
const BASE_PADDING_PX = 100;

/**
 * The single "source" card — one accordion root per selected resource, sibling roots (mirroring the
 * mockup's multi-root TREE). Cycles the design-spec's rank-color palette (--fm-rank-1..11) per
 * resource root purely by CSS-variable name, never a raw hex, via a scoped --fm-group-color.
 */
@Component({
  selector: 'app-field-mapping-source-tree',
  standalone: true,
  imports: [FieldMappingTreeNodeComponent],
  templateUrl: './field-mapping-source-tree.component.html',
  styleUrl: './field-mapping-source-tree.component.scss',
})
export class FieldMappingSourceTreeComponent implements AfterViewInit, OnDestroy {
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly card = viewChild.required<ElementRef<HTMLElement>>('card');
  private resizeObserver: ResizeObserver | null = null;

  readonly forest = input.required<FmTreeNode[]>();
  readonly isCollapsed = input.required<(id: string) => boolean>();
  readonly isMapped = input.required<(id: string) => boolean>();
  readonly isArmed = input.required<(id: string) => boolean>();
  readonly x = input.required<number>();
  readonly y = input.required<number>();

  readonly toggleCollapse = output<string>();
  readonly armToggle = output<FmTreeNode>();
  readonly dragStart = output<FmDragStart>();
  readonly dragMove = output<FmDragMove>();
  readonly dragEnd = output<FmDragEnd>();
  readonly positionChange = output<{ x: number; y: number }>();

  private dragOffset: { dx: number; dy: number } | null = null;

  // ── search ────────────────────────────────────────────────────────────────
  // Filters this card's own forest client-side (no round trip — the whole forest is already in
  // memory). Kept local to this component: the parent's collapse-state map (isCollapsed) is only
  // consulted while NOT searching (see effectiveIsCollapsed) so it never has to know about this.
  readonly searchQuery = signal('');
  readonly isSearching = computed(() => this.searchQuery().trim().length > 0);

  /** Filtered roots paired with their ORIGINAL forest index, so a resource's rank color never shifts
   *  as filtering changes which index it lands on within the filtered array. */
  readonly filteredRoots = computed(() => {
    const original = this.forest();
    return filterForest(original, this.searchQuery())
      .map(node => ({ node, colorIndex: original.findIndex(r => r.id === node.id) }));
  });

  readonly matchCount = computed(() =>
    this.isSearching() ? this.filteredRoots().reduce((sum, r) => sum + flattenLeaves(r.node).length, 0) : 0,
  );

  /** While searching, force every surviving node open so matches are actually visible — the caller's
   *  own fold/collapse state (isCollapsed) only applies when there's no active query. */
  readonly effectiveIsCollapsed = computed(() => {
    if (!this.isSearching()) return this.isCollapsed();
    return () => false;
  });

  onSearchInput(value: string): void { this.searchQuery.set(value); }
  clearSearch(): void { this.searchQuery.set(''); }

  ngAfterViewInit(): void {
    // One-time default sized to fit the longest label actually in this resource's tree, in place of the
    // old flat 400px default — never re-applied afterward (see field-mapping-card-size.util.ts), so it
    // can't fight the user's own drag-resize later.
    this.card().nativeElement.style.width = `${autoCardWidth(this.labelLengths(this.forest()), BASE_PADDING_PX, MIN_WIDTH, MAX_WIDTH)}px`;

    // The card is now user-resizable (CSS `resize: both`), which doesn't fire any DOM event on its
    // own — without this, the wires attached to rows inside it would visually lag behind a drag-resize
    // until some unrelated action happened to bump the anchor registry's version.
    this.resizeObserver = new ResizeObserver(() => this.anchors.refreshAll());
    this.resizeObserver.observe(this.card().nativeElement);
  }

  /** Every node's label length, weighted by nesting depth to roughly account for indentation — a deeply
   *  nested short label can still need as much horizontal room as a shallow long one. */
  private labelLengths(nodes: FmTreeNode[], depth = 0): number[] {
    const out: number[] = [];
    for (const node of nodes) {
      out.push(node.label.length + depth * 2);
      out.push(...this.labelLengths(node.children, depth + 1));
    }
    return out;
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
  }

  onHeadPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
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

  groupColorVar(index: number): string {
    return `var(--fm-rank-${(index % 11) + 1})`;
  }
}
