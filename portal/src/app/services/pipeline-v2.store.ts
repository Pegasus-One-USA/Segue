import { Injectable, computed, signal } from '@angular/core';

// re-export CanvasNode and CanvasEdge from models/index.ts when that exists;
// for now import individually
import { CanvasNode as CN, TransformNode } from '../models/node-v2.model';
import { CanvasEdge as CE } from '../models/edge-v2.model';
import { TRANSFORMS } from '../data/transforms-v2.data';

const SOURCE_RANK_VALUE = 0;

@Injectable({ providedIn: 'root' })
export class PipelineStoreV2 {
  // ── core signals ──────────────────────────────────────────────────────────
  readonly nodes = signal<CN[]>([]);
  readonly edges = signal<CE[]>([]);
  readonly pan    = signal<{ x: number; y: number }>({ x: 0, y: 0 });
  readonly zoom   = signal<number>(1);
  readonly editingNodeId = signal<string | null>(null);
  readonly tempConnectorPath = signal<string | null>(null);

  // Monotonic counter appended to every generated id. Date.now() alone has 1ms resolution and multiple
  // ids are routinely minted within the same tick (e.g. auto-inserting a Mapping node followed immediately
  // by its destination node), which produced duplicate ids and a dictionary-key collision at build time.
  private idSequence = 0;

  // ── computed ──────────────────────────────────────────────────────────────
  readonly nodeMap = computed(() => {
    const map = new Map<string, CN>();
    this.nodes().forEach(n => map.set(n.id, n));
    return map;
  });

  readonly isEmpty = computed(() => this.nodes().length === 0);

  // ── dirty tracking ──────────────────────────────────────────────────────────
  // Bumped by every canvas mutation below; `savedRevision` is snapshotted wherever the canvas
  // matches what's actually persisted (fresh load, reset, or a successful save) — the unsaved-
  // changes navigation guard reads `dirty` to decide whether leaving needs a confirmation.
  private readonly revision = signal(0);
  private readonly savedRevision = signal(0);
  readonly dirty = computed(() => this.revision() !== this.savedRevision());

  private bump(): void {
    this.revision.update(v => v + 1);
  }

  /** Call after a save actually persists — marks the current canvas state as the clean baseline. */
  markSaved(): void {
    this.savedRevision.set(this.revision());
  }

  /**
   * Marks the canvas as carrying unsaved changes. For mutations that don't go through the node/edge CRUD
   * below — clearing an already-saved workflow's canvas is still a change the user has to be able to save
   * (see reset(), which deliberately syncs clean because its other caller blanks a brand-new workflow).
   */
  markDirty(): void {
    this.bump();
  }

  private syncClean(): void {
    this.bump();
    this.markSaved();
  }

  // ── node CRUD ──────────────────────────────────────────────────────────────
  addNode(node: CN): void {
    this.nodes.update(ns => [...ns, node]);
    this.bump();
  }

  removeNode(id: string): void {
    this.nodes.update(ns => ns.filter(n => n.id !== id));
    this.edges.update(es => es.filter(e => e.from !== id && e.to !== id));
    this.bump();
  }

  updateNode(id: string, patch: Partial<CN>): void {
    this.nodes.update(ns =>
      ns.map(n => (n.id === id ? { ...n, ...patch } as CN : n))
    );
    this.bump();
  }

  moveNode(id: string, x: number, y: number): void {
    this.nodes.update(ns =>
      ns.map(n => (n.id === id ? { ...n, x, y } : n))
    );
    this.bump();
  }

  /** Swaps a node's client-synthetic id (nextNodeId()/nextTransformId()/…) for its real server-assigned id
   *  once an incremental add persists — see WorkflowBuilderV2Component.insertChainStep. Rewrites every edge
   *  endpoint referencing the old id too, so a chain step wired up synchronously (before the server call
   *  resolves) stays correctly connected under its new id. No-op if oldId isn't found. */
  renameNodeId(oldId: string, newId: string): void {
    if (oldId === newId) return;
    this.nodes.update(ns => ns.map(n => (n.id === oldId ? { ...n, id: newId } : n)));
    this.edges.update(es => es.map(e => ({
      ...e,
      from: e.from === oldId ? newId : e.from,
      to: e.to === oldId ? newId : e.to,
    })));
    if (this.editingNodeId() === oldId) this.editingNodeId.set(newId);
    this.bump();
  }

  // ── edge CRUD ──────────────────────────────────────────────────────────────
  addEdge(edge: CE): void {
    this.edges.update(es => [...es, edge]);
    this.bump();
  }

  loadGraph(nodes: CN[], edges: CE[]): void {
    this.nodes.set(nodes);
    this.edges.set(edges);
    this.editingNodeId.set(null);
    this.tempConnectorPath.set(null);
    this.syncClean();
  }

  removeEdge(id: string): void {
    this.edges.update(es => es.filter(e => e.id !== id));
    this.bump();
  }

  hasEdge(fromId: string, toId: string): boolean {
    return this.edges().some(e => e.from === fromId && e.to === toId);
  }

  // ── canvas reset ───────────────────────────────────────────────────────────
  reset(): void {
    this.nodes.set([]);
    this.edges.set([]);
    this.pan.set({ x: 0, y: 0 });
    this.zoom.set(1);
    this.editingNodeId.set(null);
    this.tempConnectorPath.set(null);
    this.syncClean();
  }

  // ── lookups ────────────────────────────────────────────────────────────────
  byId(id: string): CN | undefined {
    return this.nodeMap().get(id);
  }

  inboundEdges(nodeId: string): CE[] {
    return this.edges().filter(e => e.to === nodeId);
  }

  outboundEdges(nodeId: string): CE[] {
    return this.edges().filter(e => e.from === nodeId);
  }

  // ── pipeline helpers ───────────────────────────────────────────────────────
  nodeRank(node: CN): number {
    if (node.kind === 'merge') {
      const t = TRANSFORMS.find(x => x.group === node.group);
      return t ? t.rank : 0;
    }
    if (node.kind === 'transform') {
      const t = TRANSFORMS.find(x => x.id === node.transformId);
      return t ? t.rank : 6;
    }
    return SOURCE_RANK_VALUE;
  }

  rootSourceOf(node: CN): CN | undefined {
    let cur: CN | undefined = node;
    let guard = 0;
    while (cur && (cur.kind === 'transform' || cur.kind === 'merge') && guard++ < 50) {
      const inbound = this.edges().find(e => e.to === cur!.id);
      cur = inbound ? this.byId(inbound.from) : undefined;
    }
    return cur && !cur.kind ? cur : undefined;
  }

  parentOf(nodeId: string): CN | undefined {
    const inbound = this.edges().find(e => e.to === nodeId);
    return inbound ? this.byId(inbound.from) : undefined;
  }

  directTransformChildren(nodeId: string): TransformNode[] {
    return this.edges()
      .filter(e => e.from === nodeId)
      .map(e => this.byId(e.to))
      .filter((n): n is TransformNode => !!n && n.kind === 'transform');
  }

  existingTransformsFrom(originNode: CN): string[] {
    const ids = new Set<string>();
    if (originNode.kind === 'transform') ids.add(originNode.transformId);
    const seen = new Set<string>();
    const visit = (id: string): void => {
      if (seen.has(id)) return;
      seen.add(id);
      this.edges().filter(e => e.from === id).forEach(e => {
        const n = this.byId(e.to);
        if (!n) return;
        if (n.kind === 'transform' && !ids.has(n.transformId)) ids.add(n.transformId);
        visit(n.id);
      });
    };
    visit(originNode.id);
    return [...ids];
  }

  assembledGroup(nodeId: string): { group: string; added: string[] } | null {
    const counts: Record<string, string[]> = {};
    this.directTransformChildren(nodeId).forEach(k => {
      const t = TRANSFORMS.find(x => x.id === k.transformId);
      const g = t?.group;
      if (g) {
        if (!counts[g]) counts[g] = [];
        counts[g].push(k.transformId);
      }
    });
    let best: string | null = null;
    Object.keys(counts).forEach(g => {
      if (!best || counts[g].length > counts[best].length) best = g;
    });
    return best ? { group: best, added: counts[best] } : null;
  }

  plusEnabled(node: CN): boolean {
    const mergeChild = this.edges().some(e => {
      if (e.from !== node.id) return false;
      const t = this.byId(e.to);
      return t?.kind === 'merge';
    });
    if (mergeChild) return false;

    const kids = this.directTransformChildren(node.id);
    if (kids.length === 0) return true;

    const allGrouped = kids.every(k => {
      const t = TRANSFORMS.find(x => x.id === k.transformId);
      return !!t?.group;
    });
    if (!allGrouped) return false;

    const merged = kids.some(k =>
      this.edges().some(e => {
        if (e.from !== k.id) return false;
        const t = this.byId(e.to);
        return t?.kind === 'merge';
      })
    );
    return !merged;
  }

  nextNodeId(): string {
    return 'n' + Date.now() + '-' + (++this.idSequence);
  }

  nextEdgeId(): string {
    return 'e' + this.edges().length + '-' + (++this.idSequence);
  }

  nextTransformId(): string {
    return 't' + Date.now() + '-' + (++this.idSequence);
  }

  nextMergeId(): string {
    return 'm' + Date.now() + '-' + (++this.idSequence);
  }
}
