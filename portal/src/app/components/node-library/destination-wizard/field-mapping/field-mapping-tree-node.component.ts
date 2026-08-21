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
  readonly isCollapsed = input.required<(id: string) => boolean>();
  readonly isMapped = input.required<(id: string) => boolean>();
  readonly isArmed = input.required<(id: string) => boolean>();

  readonly toggleCollapse = output<string>();
  readonly armToggle = output<FmTreeNode>();
  readonly dragStart = output<FmDragStart>();
  readonly dragMove = output<FmDragMove>();
  readonly dragEnd = output<FmDragEnd>();

  ngAfterViewInit(): void {
    this.anchors.register(this.node().id, this.row().nativeElement);
  }

  ngOnDestroy(): void {
    this.anchors.unregister(this.node().id);
  }

  label(): string { return this.node().label; }
  isGroup(): boolean { return this.node().kind === 'group'; }
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
    this.dragEnd.emit({ clientX: ev.clientX, clientY: ev.clientY });
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
