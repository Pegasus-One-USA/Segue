import { Component, input, output, inject } from '@angular/core';
import { CanvasNode, MergeNode } from '../../../models/node-v2.model';
import { PipelineStoreV2 } from '../../../services/pipeline-v2.store';
import { ApplicabilityServiceV2 } from '../../../services/applicability-v2.service';

@Component({
  selector: 'app-merge-node',
  standalone: true,
  imports: [],
  templateUrl: './merge-node.component.html',
  styleUrl: './merge-node.component.scss',
  host: { class: 'node', '[style.left.px]': 'node().x', '[style.top.px]': 'node().y' },
})
export class MergeNodeComponent {
  private readonly store  = inject(PipelineStoreV2);
  private readonly appSvc = inject(ApplicabilityServiceV2);

  readonly node = input.required<CanvasNode>();
  /** Hides the delete/add-next buttons — set by canvas.component from the builder's canMutate(). */
  readonly readOnly = input(false);
  /** False when this node's pipeline already has every applicable next step — the `+` is
   *  hidden rather than opening an empty library (see ApplicabilityServiceV2.canAddNext). */
  readonly canAddNext = input<boolean>(true);

  readonly delete  = output<string>();
  readonly addNext = output<string>();
  readonly dragMove  = output<{ nodeId: string; x: number; y: number }>();
  readonly portStart = output<{ nodeId: string; fromX: number; fromY: number }>();
  readonly portMove  = output<{ clientX: number; clientY: number }>();
  readonly portEnd   = output<{ nodeId: string; clientX: number; clientY: number }>();

  protected mergeLabel(): string {
    return this.appSvc.groupLabel((this.node() as MergeNode).group ?? '');
  }

  protected inCount(): number {
    return this.store.inboundEdges(this.node().id).length;
  }

  protected name(): string {
    return this.node().fields?.['__name'] ?? 'Merge';
  }

  onAddNextClick(e: MouseEvent): void {
    e.stopPropagation();
    this.addNext.emit(this.node().id);
  }

  onDeleteClick(e: MouseEvent): void {
    e.stopPropagation();
    this.delete.emit(this.node().id);
  }

  // ── drag ──────────────────────────────────────────────────────────────────
  private drag: { sX: number; sY: number; nX: number; nY: number } | null = null;

  onCirclePointerDown(e: PointerEvent): void {
    if (e.button !== 0) return; e.stopPropagation();
    this.drag = { sX: e.clientX, sY: e.clientY, nX: this.node().x, nY: this.node().y };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  }

  onCirclePointerMove(e: PointerEvent): void {
    if (!this.drag) return;
    this.dragMove.emit({ nodeId: this.node().id, x: Math.round(this.drag.nX + e.clientX - this.drag.sX), y: Math.round(this.drag.nY + e.clientY - this.drag.sY) });
  }

  onCirclePointerUp(e: PointerEvent): void {
    if (!this.drag) return;
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch { /* pointer already released */ }
    this.drag = null;
  }

  // ── port ──────────────────────────────────────────────────────────────────
  onPortPointerDown(e: PointerEvent): void {
    e.stopPropagation(); e.preventDefault();
    this.portStart.emit({ nodeId: this.node().id, fromX: this.node().x, fromY: this.node().y });
    const move = (ev: PointerEvent) => this.portMove.emit({ clientX: ev.clientX, clientY: ev.clientY });
    const up   = (ev: PointerEvent) => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      this.portEnd.emit({ nodeId: this.node().id, clientX: ev.clientX, clientY: ev.clientY });
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
  }
}
