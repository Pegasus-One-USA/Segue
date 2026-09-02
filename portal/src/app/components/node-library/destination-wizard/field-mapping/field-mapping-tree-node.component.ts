import {
  Component, ElementRef, inject, input, output, viewChild, AfterViewInit, OnDestroy,
} from '@angular/core';
import { FmTreeNode } from './field-mapping-tree.util';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';

export interface FmDragStart {
  node: FmTreeNode;
  pointerId: number;
  clientX: number;
  clientY: number;
}
export interface FmDragMove { clientX: number; clientY: number }
export interface FmDragEnd { clientX: number; clientY: number }
export interface FmFieldClick { node: FmTreeNode; anchor: HTMLElement }

/**
 * One recursive row in the source tree — a group header (accordion, foldable, itself a valid
 * whole-node-as-JSON drag source) or a leaf field. Handles pointer-capture-based drag start/move/end
 * and the keyboard "arm" equivalent (Enter/Space), per the plan's accessibility requirement: dragging
 * is one way to create a mapping, never the only way.
 */
@Component({
  selector: 'app-field-mapping-tree-node',
  standalone: true,
  imports: [FieldMappingTreeNodeComponent],
  templateUrl: './field-mapping-tree-node.component.html',
  styleUrl: './field-mapping-tree-node.component.scss',
})
export class FieldMappingTreeNodeComponent implements AfterViewInit, OnDestroy {
  private readonly anchors = inject(FieldMappingAnchorService);
  private readonly row = viewChild.required<ElementRef<HTMLElement>>('row');

  readonly node = input.required<FmTreeNode>();
  readonly depth = input<number>(0);
  /** This row's position (0-based) among its own sibling nodes — NOT a global row count. Used only to
   *  zebra-stripe alternating rows; `:nth-child` can't do this itself because each row is wrapped in its
   *  own `<app-field-mapping-tree-node>` host (recursion), so rows are never real DOM siblings of each
   *  other even though `:host { display: contents }` makes them render flush together. */
  readonly siblingIndex = input<number>(0);
  readonly isCollapsed = input.required<(id: string) => boolean>();
  readonly isMapped = input.required<(id: string) => boolean>();
  readonly isArmed = input.required<(id: string) => boolean>();

  readonly toggleCollapse = output<string>();
  readonly armToggle = output<FmTreeNode>();
  readonly dragStart = output<FmDragStart>();
  readonly dragMove = output<FmDragMove>();
  readonly dragEnd = output<FmDragEnd>();
  /** A leaf was pressed and released with essentially no movement in between — i.e. a plain click, not
   *  a drag — so a mapped leaf can open its own Mapping Configuration (or a "choose which mapping"
   *  popover, if it's the source of more than one) instead of only being reachable by dragging a wire
   *  onto a target or by the connector line itself. Deliberately leaf-only (see onPointerUp): a group
   *  header's plain click already means "expand/collapse" (onHeaderClick below), a long-established
   *  gesture this must not also hijack. */
  readonly fieldClick = output<FmFieldClick>();

  private pointerDownAt: { x: number; y: number } | null = null;
  /** Below this many px of total movement between press and release, the gesture reads as a click
   *  rather than a drag — generous enough to absorb ordinary hand tremor/trackpad jitter without ever
   *  mistaking an actual short drag (onto a target row right next to the source tree) for a click. */
  private static readonly CLICK_MOVE_THRESHOLD_PX = 4;

  ngAfterViewInit(): void {
    this.anchors.register(this.node().id, this.row().nativeElement);
  }

  ngOnDestroy(): void {
    this.anchors.unregister(this.node().id);
  }

  label(): string { return this.node().label; }
  isGroup(): boolean { return this.node().kind === 'group'; }
  isStripe(): boolean { return this.siblingIndex() % 2 === 1; }

  /** e.g. "Patient, Group" for Observation.subject — empty when this leaf isn't a reference field, or
   *  the backend catalog didn't carry the metadata (built-in fallback defs). See
   *  ResourceFieldDef.referenceTargetTypes for why this exists: a field named "Subject" gives no hint
   *  by itself that it's the Patient link. */
  referenceTargetTypes(): string[] {
    return this.node().field?.referenceTargetTypes ?? [];
  }

  leafTooltip(): string {
    const types = this.referenceTargetTypes();
    return types.length ? `${this.node().id} — may reference: ${types.join(', ')}` : this.node().id;
  }
  fieldCount(): number {
    return this.node().kind === 'leaf' ? 1 : countLeaves(this.node());
  }

  ariaLabel(): string {
    const n = this.node();
    return n.kind === 'group'
      ? `${n.label}${n.isArray ? ', repeating group' : ''}, ${this.fieldCount()} field${this.fieldCount() === 1 ? '' : 's'}`
      : `${n.label}${n.field?.arrays?.length ? ', array field' : ''}`;
  }

  onPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    const el = this.row().nativeElement;
    el.setPointerCapture(ev.pointerId);
    this.pointerDownAt = { x: ev.clientX, y: ev.clientY };
    this.dragStart.emit({ node: this.node(), pointerId: ev.pointerId, clientX: ev.clientX, clientY: ev.clientY });
    ev.preventDefault();
  }

  onPointerMove(ev: PointerEvent): void {
    if (!this.row().nativeElement.hasPointerCapture(ev.pointerId)) return;
    this.dragMove.emit({ clientX: ev.clientX, clientY: ev.clientY });
  }

  onPointerUp(ev: PointerEvent): void {
    const el = this.row().nativeElement;
    if (!el.hasPointerCapture(ev.pointerId)) return;
    el.releasePointerCapture(ev.pointerId);
    const downAt = this.pointerDownAt;
    this.pointerDownAt = null;
    this.dragEnd.emit({ clientX: ev.clientX, clientY: ev.clientY });

    // A real drag already completed (or no-op'd) via dragEnd above regardless — this only adds the
    // click notification on top, for a leaf released close enough to where it was pressed.
    if (!this.isGroup() && downAt) {
      const moved = Math.hypot(ev.clientX - downAt.x, ev.clientY - downAt.y);
      if (moved <= FieldMappingTreeNodeComponent.CLICK_MOVE_THRESHOLD_PX) {
        this.fieldClick.emit({ node: this.node(), anchor: el });
      }
    }
  }

  onKeydown(ev: KeyboardEvent): void {
    if (ev.key !== 'Enter' && ev.key !== ' ') return;
    ev.preventDefault();
    this.armToggle.emit(this.node());
  }

  onHeaderClick(): void {
    if (this.isGroup()) this.toggleCollapse.emit(this.node().id);
  }
}

function countLeaves(node: FmTreeNode): number {
  if (node.kind === 'leaf') return 1;
  return node.children.reduce((sum, c) => sum + countLeaves(c), 0);
}
