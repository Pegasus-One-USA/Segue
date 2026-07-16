import { Component, inject, input, output } from '@angular/core';
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
export class FieldMappingSourceTreeComponent {
  private readonly anchors = inject(FieldMappingAnchorService);

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
