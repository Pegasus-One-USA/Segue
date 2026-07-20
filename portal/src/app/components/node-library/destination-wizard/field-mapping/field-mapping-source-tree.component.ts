import { AfterViewInit, Component, ElementRef, OnDestroy, inject, input, output, viewChild } from '@angular/core';
import { FmTreeNode } from './field-mapping-tree.util';
import { FieldMappingTreeNodeComponent, FmDragStart, FmDragMove, FmDragEnd } from './field-mapping-tree-node.component';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

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

  ngAfterViewInit(): void {
    // The card is now user-resizable (CSS `resize: both`), which doesn't fire any DOM event on its
    // own — without this, the wires attached to rows inside it would visually lag behind a drag-resize
    // until some unrelated action happened to bump the anchor registry's version.
    this.resizeObserver = new ResizeObserver(() => this.anchors.refreshAll());
    this.resizeObserver.observe(this.card().nativeElement);
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
