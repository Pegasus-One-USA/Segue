import {
  Component, inject, computed, signal, ElementRef, viewChild, output, input,
} from '@angular/core';
import { PipelineStore } from '../../services/pipeline.store';
import { CanvasService } from '../../services/canvas.service';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityService } from '../../services/applicability.service';
import { CanvasNode, TransformNode } from '../../models/node.model';
import { SourceNodeComponent } from '../nodes/source-node/source-node.component';
import { TransformNodeComponent } from '../nodes/transform-node/transform-node.component';
import { MergeNodeComponent } from '../nodes/merge-node/merge-node.component';
import { CanvasConnectorsComponent } from './canvas-connectors/canvas-connectors.component';
import { ZoomDockComponent } from './zoom-dock/zoom-dock.component';

@Component({
  selector: 'app-canvas',
  standalone: true,
  imports: [
    SourceNodeComponent,
    TransformNodeComponent,
    MergeNodeComponent,
    CanvasConnectorsComponent,
    ZoomDockComponent,
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
  /** Node-level "Copy checkpoint URL" (Phase 1) — the parent owns the saved workflow id, so it makes the API call. */
  readonly copyCheckpointUrl   = output<string>();

  /** True once the workflow has a saved id — a checkpoint URL can only be generated against a persisted node. */
  readonly workflowSaved = input<boolean>(false);

  // ── computed view helpers ─────────────────────────────────────────────────
  protected readonly nodes    = this.store.nodes;
  protected readonly edges    = this.store.edges;
  protected readonly isEmpty  = this.store.isEmpty;
  protected readonly tempPath = this.store.tempConnectorPath;

  protected readonly transformStyle = this.canvas.transformStyle;
  protected readonly zoomPercent    = this.canvas.zoomPercent;

  protected isSourceNode(n: CanvasNode): boolean    { return !n.kind; }
  protected isTransformNode(n: CanvasNode): boolean { return n.kind === 'transform'; }
  protected isMergeNode(n: CanvasNode): boolean     { return n.kind === 'merge'; }

  // ── pan ───────────────────────────────────────────────────────────────────
  private panning: { px: number; py: number; x: number; y: number } | null = null;

  onShellPointerDown(e: PointerEvent): void {
    const el = e.target as HTMLElement;
    if (el.closest('.node,.add-module-fab,.ctx-menu,.ctx-backdrop,.zoom-dock,.confirm-backdrop,.confirm-dialog')) return;
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
      try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch { /* pointer already released */ }
    }
    this.panning = null;
  }

  onWheel(e: WheelEvent): void {
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

  // ── node delete (with confirmation) ──────────────────────────────────────
  protected readonly pendingDeleteId = signal<string | null>(null);

  onNodeDelete(nodeId: string): void {
    if (this.isFieldMappingNode(nodeId)) return;
    this.pendingDeleteId.set(nodeId);
  }

  /** Field Mapping is a required step once a destination is attached — never user-deletable,
      regardless of entry point (hover button or context menu). */
  private isFieldMappingNode(id: string | null | undefined): boolean {
    const node = id ? this.store.byId(id) : undefined;
    return node?.kind === 'transform' && (node as TransformNode).transformId === 'field-mapping';
  }

  confirmDelete(): void {
    const id = this.pendingDeleteId();
    if (!id) return;
    this.store.removeNode(id);
    this.toast.show('Node removed', 'Node and its connections deleted.');
    this.pendingDeleteId.set(null);
  }

  cancelDelete(): void {
    this.pendingDeleteId.set(null);
  }

  onConfirmBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelDelete();
  }

  // ── configure (opens wizard) ──────────────────────────────────────────────
  onNodeConfigure(nodeId: string): void {
    this.openWizard.emit(nodeId);
  }

  // ── add transform picker ──────────────────────────────────────────────────
  onAddNext(nodeId: string): void {
    this.openTransformPicker.emit(nodeId);
  }

  // ── clipboard ─────────────────────────────────────────────────────────────
  private readonly clipboard = signal<CanvasNode | null>(null);
  protected readonly hasClipboard = computed(() => !!this.clipboard());

  // ── context menu ──────────────────────────────────────────────────────────
  protected readonly ctxMenu = signal<{
    type: 'canvas' | 'node';
    x: number; y: number;
    flowX: number; flowY: number;
    nodeId?: string;
  } | null>(null);

  onContextMenu(e: MouseEvent): void {
    e.preventDefault();
    const el = e.target as HTMLElement;
    if (el.closest('.ctx-menu,.confirm-backdrop,.confirm-dialog')) return;

    const shell = this.shellEl()?.nativeElement;
    const rect  = shell?.getBoundingClientRect();
    const flow  = rect ? this.canvas.clientToFlow(e.clientX, e.clientY, rect) : { x: 400, y: 300 };

    const nodeEl = el.closest('[data-node-id]') as HTMLElement | null;
    if (nodeEl?.dataset['nodeId']) {
      this.ctxMenu.set({ type: 'node', x: e.clientX, y: e.clientY, flowX: flow.x, flowY: flow.y, nodeId: nodeEl.dataset['nodeId'] });
    } else {
      this.ctxMenu.set({ type: 'canvas', x: e.clientX, y: e.clientY, flowX: flow.x, flowY: flow.y });
    }
  }

  closeCtx(): void { this.ctxMenu.set(null); }

  ctxCopy(): void {
    const id   = this.ctxMenu()?.nodeId;
    const node = id ? this.store.byId(id) : null;
    if (node) {
      this.clipboard.set(node);
      this.toast.show('Copied', 'Node copied to clipboard.');
    }
    this.ctxMenu.set(null);
  }

  ctxPaste(): void {
    const node = this.clipboard();
    const menu = this.ctxMenu();
    if (!node || !menu) return;
    const newId = node.kind === 'transform' ? this.store.nextTransformId()
                : node.kind === 'merge'     ? this.store.nextMergeId()
                : this.store.nextNodeId();
    this.store.addNode({ ...node, id: newId, x: menu.flowX, y: menu.flowY } as CanvasNode);
    this.toast.show('Pasted', 'Node pasted onto canvas.');
    this.ctxMenu.set(null);
  }

  ctxAddModule(): void {
    this.openSourcePicker.emit();
    this.ctxMenu.set(null);
  }

  ctxDelete(): void {
    const id = this.ctxMenu()?.nodeId;
    if (id) this.onNodeDelete(id);
    this.ctxMenu.set(null);
  }

  // ── checkpoint (Phase 1) ───────────────────────────────────────────────────
  /** True for the node currently under the context menu — drives which checkpoint action(s) the menu shows. */
  protected checkpointEnabledForCtxNode(): boolean {
    const id = this.ctxMenu()?.nodeId;
    const node = id ? this.store.byId(id) : undefined;
    return !!node?.checkpointUrlEnabled;
  }

  /** Merge nodes are a client-side-only grouping concept (never sent to the backend) — no checkpoint actions for them. */
  protected ctxNodeIsMerge(): boolean {
    const id = this.ctxMenu()?.nodeId;
    const node = id ? this.store.byId(id) : undefined;
    return node?.kind === 'merge';
  }

  /** Drives whether the context menu's Delete item shows for the node under it. */
  protected ctxNodeIsFieldMapping(): boolean {
    return this.isFieldMappingNode(this.ctxMenu()?.nodeId);
  }

  ctxToggleCheckpoint(): void {
    const id = this.ctxMenu()?.nodeId;
    const node = id ? this.store.byId(id) : undefined;
    if (!id || !node) { this.ctxMenu.set(null); return; }

    const enabling = !node.checkpointUrlEnabled;
    this.store.updateNode(id, { checkpointUrlEnabled: enabling });
    this.toast.show(
      enabling ? 'Checkpoint enabled' : 'Checkpoint disabled',
      enabling
        ? 'Save the workflow, then use "Copy checkpoint URL" on this node.'
        : 'This node no longer offers a checkpoint URL once you save.',
    );
    this.ctxMenu.set(null);
  }

  ctxCopyCheckpointUrl(): void {
    const id = this.ctxMenu()?.nodeId;
    this.ctxMenu.set(null);
    if (id) this.copyCheckpointUrl.emit(id);
  }
}
