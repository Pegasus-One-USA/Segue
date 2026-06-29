import {
  Component, inject, computed, signal, ElementRef, viewChild,
  output, HostListener,
} from '@angular/core';
import { PipelineStore } from '../../services/pipeline.store';
import { CanvasService } from '../../services/canvas.service';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityService } from '../../services/applicability.service';
import { CanvasNode, isTransformNode, isMergeNode } from '../../models/node.model';
import { TRANSFORMS } from '../../data/transforms.data';
import { SourceNodeComponent } from '../nodes/source-node/source-node.component';
import { TransformNodeComponent } from '../nodes/transform-node/transform-node.component';
import { MergeNodeComponent } from '../nodes/merge-node/merge-node.component';
import { CanvasConnectorsComponent } from './canvas-connectors/canvas-connectors.component';
import { ZoomDockComponent } from './zoom-dock/zoom-dock.component';
import { HintCardComponent } from './hint-card/hint-card.component';

@Component({
  selector: 'app-canvas',
  standalone: true,
  imports: [
    SourceNodeComponent,
    TransformNodeComponent,
    MergeNodeComponent,
    CanvasConnectorsComponent,
    ZoomDockComponent,
    HintCardComponent,
  ],
  templateUrl: './canvas.component.html',
  styleUrl: './canvas.component.scss',
})
export class CanvasComponent {
  protected readonly store   = inject(PipelineStore);
  protected readonly canvas  = inject(CanvasService);
  protected readonly toast   = inject(ToastService);
  protected readonly appSvc  = inject(ApplicabilityService);

  // ── events upward ─────────────────────────────────────────────────────────
  readonly openWizard          = output<string | undefined>();
  readonly openTransformPicker = output<string>();
  readonly openSourcePicker    = output<void>();

  // ── computed view helpers ─────────────────────────────────────────────────
  protected readonly nodes    = this.store.nodes;
  protected readonly edges    = this.store.edges;
  protected readonly isEmpty  = this.store.isEmpty;
  protected readonly tempPath = this.store.tempConnectorPath;

  protected readonly transformStyle = this.canvas.transformStyle;
  protected readonly zoomPercent    = this.canvas.zoomPercent;

  protected isSourceNode(n: CanvasNode): boolean  { return !n.kind; }
  protected isTransformNode(n: CanvasNode): boolean { return n.kind === 'transform'; }
  protected isMergeNode(n: CanvasNode): boolean   { return n.kind === 'merge'; }

  protected plusEnabled(n: CanvasNode): boolean {
    return this.store.plusEnabled(n);
  }

  // ── pan ───────────────────────────────────────────────────────────────────
  private panning: { px: number; py: number; x: number; y: number } | null = null;

  onShellPointerDown(e: PointerEvent): void {
    const el = e.target as HTMLElement;
    if (el.closest('.node,.mega-add,.add-module-fab,.zoom-dock,.hint-card,.scenario-meta')) return;
    const { x, y } = this.canvas.pan();
    this.panning = { px: e.clientX, py: e.clientY, x, y };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  }

  onShellPointerMove(e: PointerEvent): void {
    if (!this.panning) return;
    this.canvas.setPan(
      this.panning.x + (e.clientX - this.panning.px),
      this.panning.y + (e.clientY - this.panning.py),
    );
  }

  onShellPointerUp(e: PointerEvent): void {
    if (this.panning) {
      try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch {}
    }
    this.panning = null;
  }

  onWheel(e: WheelEvent): void {
    if (!e.ctrlKey && !e.metaKey) return;
    e.preventDefault();
    const shell = (e.currentTarget as HTMLElement).getBoundingClientRect();
    this.canvas.setZoom(
      this.canvas.zoom() * (e.deltaY < 0 ? 1.1 : 0.9),
      e.clientX - shell.left,
      e.clientY - shell.top,
    );
  }

  // ── node drag ─────────────────────────────────────────────────────────────
  onNodeDragMove(e: { nodeId: string; x: number; y: number }): void {
    this.store.moveNode(e.nodeId, e.x, e.y);
  }

  // ── port-connect ──────────────────────────────────────────────────────────
  private connectingFrom: CanvasNode | null = null;
  private readonly shellEl = viewChild<ElementRef<HTMLElement>>('shellRef');

  onPortStart(e: { nodeId: string; fromX: number; fromY: number }): void {
    this.connectingFrom = this.store.byId(e.nodeId) ?? null;
  }

  onPortMove(e: { clientX: number; clientY: number }): void {
    if (!this.connectingFrom || !this.shellEl()) return;
    const rect = this.shellEl()!.nativeElement.getBoundingClientRect();
    const fp   = this.canvas.clientToFlow(e.clientX, e.clientY, rect);
    const path = this.canvas.edgePath(
      { x: this.connectingFrom.x, y: this.connectingFrom.y },
      { x: fp.x + 60, y: fp.y },
    );
    this.store.tempConnectorPath.set(path);
  }

  onPortEnd(e: { nodeId: string; clientX: number; clientY: number }): void {
    this.store.tempConnectorPath.set(null);
    if (!this.connectingFrom) { this.connectingFrom = null; return; }

    const hit = document.elementFromPoint(e.clientX, e.clientY);
    const nodeEl = hit?.closest('[data-node-id]') as HTMLElement | null;
    const targetId = nodeEl?.dataset['nodeId'];

    if (targetId && targetId !== this.connectingFrom.id) {
      const from   = this.connectingFrom;
      const target = this.store.byId(targetId);
      if (target) {
        if (this.store.hasEdge(from.id, targetId)) {
          this.toast.show('Already connected', 'That edge already exists.');
        } else {
          const fromRank   = this.store.nodeRank(from);
          const targetRank = this.store.nodeRank(target);
          if (targetRank <= fromRank) {
            this.toast.show(
              'Backward connection blocked',
              `Pipeline runs forward only — rank ${targetRank} is at or before rank ${fromRank}.`,
            );
          } else {
            this.store.addEdge({ id: this.store.nextEdgeId(), from: from.id, to: targetId });
            this.toast.show('Connected', 'Edge created.');
          }
        }
      }
    }
    this.connectingFrom = null;
  }

  // ── node delete ───────────────────────────────────────────────────────────
  onNodeDelete(nodeId: string): void {
    this.store.removeNode(nodeId);
    this.toast.show('Node removed', 'Node and its connections deleted.');
  }

  // ── configure (opens wizard) ──────────────────────────────────────────────
  onNodeConfigure(nodeId: string): void {
    this.openWizard.emit(nodeId);
  }

  // ── add transform picker ──────────────────────────────────────────────────
  onAddNext(nodeId: string): void {
    this.openTransformPicker.emit(nodeId);
  }
}
