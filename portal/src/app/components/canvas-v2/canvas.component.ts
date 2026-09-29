import {
  Component, inject, computed, signal, ElementRef, viewChild, output, input,
} from '@angular/core';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { CanvasServiceV2 } from '../../services/canvas-v2.service';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityServiceV2 } from '../../services/applicability-v2.service';
import { CanvasNode, TransformNode } from '../../models/node-v2.model';
import { SourceNodeComponent } from '../nodes-v2/source-node/source-node.component';
import { TransformNodeComponent } from '../nodes-v2/transform-node/transform-node.component';
import { MergeNodeComponent } from '../nodes-v2/merge-node/merge-node.component';
import { CanvasConnectorsComponent } from './canvas-connectors/canvas-connectors.component';
import { ZoomDockComponent } from './zoom-dock/zoom-dock.component';
import { PermissionService } from '../../auth/services/permission.service';
import { buildNodeDeletePlanV2, NodeDeletePlan } from '../../services/node-delete-plan-v2.util';
import { sourceFormKeyForNode } from '../node-library-v2/source-node-vendor.util';
import { SOURCES } from '../../data/sources-v2.data';
import { TRANSFORMS } from '../../data/transforms-v2.data';
import { TransformationRulesService } from '../node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { DestinationType } from '../../destination-connections/models/destination-configuration.model';

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
  protected readonly store       = inject(PipelineStoreV2);
  protected readonly canvas      = inject(CanvasServiceV2);
  protected readonly toast       = inject(ToastService);
  protected readonly appSvc      = inject(ApplicabilityServiceV2);
  private readonly permissions   = inject(PermissionService);
  private readonly transformationRules = inject(TransformationRulesService);

  // ── events upward ─────────────────────────────────────────────────────────
  readonly openWizard          = output<string>();
  readonly openTransformPicker = output<string>();
  readonly openSourcePicker    = output<void>();
  /** Node-level "Copy checkpoint URL" (Phase 1) — the parent owns the saved workflow id, so it makes the API call. */
  readonly copyCheckpointUrl   = output<string>();

  /** True once the workflow has a saved id — a checkpoint URL can only be generated against a persisted node. */
  readonly workflowSaved = input<boolean>(false);

  /** True when the current role lacks the permission to mutate THIS workflow (workflow.create for a
   *  brand-new one, workflow.edit for an existing one — see WorkflowBuilderComponent.canMutate). Hides
   *  every canvas action that would change the graph (add/delete/paste/checkpoint-toggle); viewing and
   *  panning/zooming/opening a node's config to look at it stay available either way. */
  readonly readOnly = input<boolean>(false);

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
  /** The pending delete, as the full plan of what it will take with it (see buildNodeDeletePlanV2) —
   *  not just the clicked node's id, because a delete here cascades along the chain and strips the
   *  configuration those steps stored on their destination. The confirmation enumerates all of it. */
  protected readonly pendingDelete = signal<NodeDeletePlan | null>(null);

  // Removing a node from the canvas requires BOTH workflow.edit/create (readOnly(), checked first) AND
  // that node's own vendor `.delete` permission (epic.delete, sqlserver.delete, ...) — the same AND
  // pattern Add/Edit already use (onSourceSelected/onTransformSelected in workflow-builder.component.ts
  // each check canMutate() AND the vendor's create/edit). This is a DIFFERENT action from deleting the
  // underlying stored Source/Destination Connection in Settings (SourceConnectionsController.cs/
  // ConfigurationsController.cs's DELETE endpoints, which additionally require sourceconnections.delete/
  // destinationconnections.delete) — the two share the same vendor code by design, not by accident: both
  // are "can this role delete Epic-related things," just at two different scopes. See
  // WorkflowEndpoints.cs's /workflows/build node-removal check for the backend half of this same rule.
  onNodeDelete(nodeId: string): void {
    if (this.readOnly()) return;
    // The template already hides this action's trigger when canDeleteNode() is false (see
    // ctxNodeCanDelete()/each node component's own [canDelete] input) — this re-check is defense-in-depth
    // against a permission revoked in another tab since the menu/icon last rendered, not the primary gate.
    if (!this.canDeleteNode(nodeId)) {
      this.toast.show('Not permitted', "You don't have permission to remove this node from the workflow.");
      return;
    }

    const plan = buildNodeDeletePlanV2(nodeId, this.store.nodes(), this.store.edges());
    if (!plan) return;

    // Every node the cascade reaches has to clear the same vendor `.delete` gate as the clicked one —
    // otherwise deleting a destination you may delete would be a way to remove a source you may not.
    const forbidden = plan.removals.find(removal => !this.canDeleteNode(removal.id));
    if (forbidden) {
      this.toast.show(
        'Not permitted',
        `Deleting this also removes ${forbidden.label}, which you don't have permission to remove.`,
      );
      return;
    }

    this.pendingDelete.set(plan);
  }

  /** Resolves the node's vendor (via source-node-vendor.util.ts for a source node, TRANSFORMS lookup for
   *  a destination node) and checks its `.delete` permission. Merge nodes and any node whose vendor can't
   *  be resolved have no vendor-level delete gate — always deletable once past readOnly(), same as before
   *  this check existed. Kept public (not private) so the template can also use it to HIDE the Delete
   *  affordance up front — this is a defense-in-depth re-check for the click itself (e.g. a permission
   *  revoked in another tab since the menu was last rendered), not the primary gate. */
  protected canDeleteNode(nodeId: string): boolean {
    const node = this.store.byId(nodeId);
    if (!node) return true;

    const prefix = node.kind === 'transform'
      ? TRANSFORMS.find(t => t.id === (node as TransformNode).transformId)?.permissionPrefix
      : node.kind === undefined
        ? SOURCES.find(s => s.id === sourceFormKeyForNode(node))?.permissionPrefix
        : undefined;
    if (!prefix) return true;

    return this.permissions.hasPermission(`${prefix}.delete`);
  }

  confirmDelete(): void {
    const plan = this.pendingDelete();
    if (!plan) return;

    // Strip the removed steps' configuration off the destinations that host it BEFORE the nodes go, so
    // the two can't get out of step if one half throws. A chain node's data lives on its destination
    // (see CHAIN_STEP_OWNED_FIELDS), so skipping this would leave the canvas carrying mappings for a
    // Mapping node that no longer exists — and the graph mapper would still send them on the next Save.
    for (const clear of plan.fieldClears) {
      const owner = this.store.byId(clear.nodeId);
      if (!owner) continue;
      const fields = { ...owner.fields };
      for (const key of clear.keys) delete fields[key];
      this.store.updateNode(clear.nodeId, { fields });
    }

    // Computed BEFORE the removals: it has to resolve the destination type of nodes that are about to go,
    // and once removeNode has run they are no longer in the store to look up.
    const orphanedDestinationTypes = this.destinationTypesLosingTheirLastNode(plan);

    for (const removal of plan.removals) this.store.removeNode(removal.id);

    // Reconnect what the removal sat between (source → destination after a Mapping delete, predecessor
    // → successor after a single step) — removeNode drops the edges on both sides, so without this the
    // surviving chain would be split into disconnected halves and fail WorkflowGraphValidator on Save.
    for (const edge of plan.relink) {
      if (!this.store.byId(edge.from) || !this.store.byId(edge.to)) continue;
      if (this.store.hasEdge(edge.from, edge.to)) continue;
      this.store.addEdge({ id: this.store.nextEdgeId(), from: edge.from, to: edge.to });
    }

    // Discard the drafts the removed destinations owned. A transformation rule is written to the server the
    // moment it is configured, but stays PENDING (no workflow) until a save attaches it — and nothing deleted
    // it when its node went, so it outlived the node. Because the pending tier matches on destination type +
    // resource type + destination field alone, re-creating the same column on a NEW node of the same type
    // re-applied that rule with nothing selected.
    //
    // Only for a destination type that has just lost its LAST node: pending rules are keyed by field, not by
    // node, so while another node of the same type is still on the canvas those rules may well belong to it.
    // Fire-and-forget on purpose — this is cleanup of drafts, and failing it must not block a canvas edit the
    // user already confirmed (the next attempt cleans up, and an unattached rule cannot run meanwhile).
    if (orphanedDestinationTypes.length > 0) {
      this.transformationRules.deletePending(orphanedDestinationTypes).subscribe({
        next: () => { /* silent: the user deleted the node, not the rules */ },
        error: () => { /* see above — drafts, not data */ },
      });
    }

    const count = plan.removals.length;
    this.toast.show(
      count === 1 ? 'Module removed' : `${count} modules removed`,
      count === 1
        ? `${plan.targetLabel} and its connections deleted.`
        : `${plan.removals.map(removal => removal.label).join(', ')} deleted.`,
    );
    this.pendingDelete.set(null);
  }

  /** The destination types whose LAST node this delete removes — the only ones whose pending rules are
   *  certainly orphaned. A type with another node still on the canvas is excluded, because pending rules
   *  carry no node id and those rows may belong to the survivor. */
  private destinationTypesLosingTheirLastNode(plan: NodeDeletePlan): DestinationType[] {
    const typeOf = (nodeId: string): DestinationType | undefined => {
      const node = this.store.byId(nodeId);
      if (!node || node.kind !== 'transform') return undefined;
      return TRANSFORMS.find(t => t.id === (node as TransformNode).transformId)?.destinationType as
        DestinationType | undefined;
    };

    const removedNodeIds = new Set(plan.removals.map(removal => removal.id));
    const surviving = new Set(this.store.nodes()
      .filter(node => !removedNodeIds.has(node.id))
      .map(node => typeOf(node.id))
      .filter((t): t is DestinationType => !!t));
    const removed = new Set([...removedNodeIds].map(typeOf)
      .filter((t): t is DestinationType => !!t));

    return [...removed].filter(destinationType => !surviving.has(destinationType));
  }

  cancelDelete(): void {
    this.pendingDelete.set(null);
  }

  onConfirmBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelDelete();
  }

  // ── configure (opens wizard) ──────────────────────────────────────────────
  onNodeConfigure(nodeId: string): void {
    this.openWizard.emit(nodeId);
  }

  /** Whether a node's `+` should be shown at all — false once its pipeline has every applicable step,
   *  so the button never opens an empty library (see ApplicabilityServiceV2.canAddNext). */
  protected canAddNext(node: CanvasNode): boolean {
    return this.appSvc.canAddNext(node, this.store.nodes(), this.store.edges());
  }

  // ── add transform picker ──────────────────────────────────────────────────
  onAddNext(nodeId: string): void {
    if (this.readOnly()) return;
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
    } else if (this.readOnly()) {
      // Empty-canvas menu has nothing but "Add module"/"Paste", both mutating — skip opening it
      // rather than popping up an empty menu.
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
    if (this.readOnly()) { this.ctxMenu.set(null); return; }
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
    if (this.readOnly()) { this.ctxMenu.set(null); return; }
    this.openSourcePicker.emit();
    this.ctxMenu.set(null);
  }

  ctxDelete(): void {
    if (this.readOnly()) { this.ctxMenu.set(null); return; }
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

  /** Drives whether the context menu's Delete item renders at all for the node currently under it —
   *  see canDeleteNode() for the actual vendor `.delete` check. */
  protected ctxNodeCanDelete(): boolean {
    const id = this.ctxMenu()?.nodeId;
    return !!id && this.canDeleteNode(id);
  }

  ctxToggleCheckpoint(): void {
    if (this.readOnly()) { this.ctxMenu.set(null); return; }
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
