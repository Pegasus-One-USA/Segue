import { Injectable } from '@angular/core';
import { TRANSFORMS } from '../data/transforms-v2.data';
import { SQL_FAMILY_DESTINATION_TYPES } from '../models/transform-v2.model';
import { EpicConfig } from '../models/transform-applicability-v2.model';
import { CanvasNode, TransformNode, isSourceNode, isMergeNode, isTransformNode } from '../models/node-v2.model';
import { PickerModel, PickerItem, MergeNodeOption } from '../models/wizard-state-v2.model';

export const SOURCE_RANK = 0;
export const DESTINATION_RANK = 1;
export const MAPPING_RANK = 2;
export const TRANSFORMATION_RANK = 3;
export const DEIDENTIFICATION_RANK = 4;

@Injectable({ providedIn: 'root' })
export class ApplicabilityServiceV2 {

  // ── helpers ──────────────────────────────────────────────────────────────
  /** V2's catalog defines no groups (see transforms-v2.data.ts) — this stays keyed off the entry so it
   *  keeps telling the truth if grouped steps are ever reintroduced, rather than hardcoding null. */
  groupOf(transformId: string): string | null {
    return TRANSFORMS.find(t => t.id === transformId)?.group ?? null;
  }

  groupLabel(group: string): string {
    if (group === 'source') return 'Source';
    return group;
  }

  groupRank(group: string): number {
    return group === 'source' ? SOURCE_RANK : 0;
  }

  nodeRankFromStore(node: CanvasNode): number {
    if (isMergeNode(node))     return this.groupRank(node.group);
    if (isTransformNode(node)) return TRANSFORMS.find(t => t.id === node.transformId)?.rank ?? DESTINATION_RANK;
    return SOURCE_RANK;
  }

  nodeDisplayName(node: CanvasNode | undefined): string {
    if (!node) return '?';
    if (isMergeNode(node))     return 'Merge · ' + this.groupLabel(node.group);
    if (isTransformNode(node)) return TRANSFORMS.find(t => t.id === node.transformId)?.name ?? 'transform';
    return node.fields['__name'] ?? 'Source';
  }

  /** True for a destination TransformNode whose catalog `destinationType` is SQL Server, Azure SQL,
   *  PostgreSQL, MySQL, or MongoDB — the only destination types Mapping applies to (see
   *  SQL_FAMILY_DESTINATION_TYPES). */
  isSqlFamilyDestination(node: CanvasNode): boolean {
    if (!isTransformNode(node)) return false;
    const destType = TRANSFORMS.find(t => t.id === node.transformId)?.destinationType;
    return !!destType && SQL_FAMILY_DESTINATION_TYPES.has(destType);
  }

  private transformItem(id: string): PickerItem | null {
    const t = TRANSFORMS.find(x => x.id === id);
    if (!t) return null;
    return { id: t.id, name: t.name, sub: t.sub, rank: t.rank, group: null, status: 'show', reason: null };
  }

  /** V2's chain steps, in the canonical order they're laid out between source and destination. */
  static readonly CHAIN_STEP_IDS = ['field-mapping', 'transformation', 'deidentification'] as const;

  isChainStep(node: CanvasNode): boolean {
    return isTransformNode(node)
      && (ApplicabilityServiceV2.CHAIN_STEP_IDS as readonly string[]).includes(node.transformId);
  }

  /**
   * The destination that terminates `node`'s pipeline. V2 lays the chain out BETWEEN source and
   * destination (Source → Mapping → Transformation → De-identification → Destination), so from a chain
   * node this walks FORWARD to the destination; a destination resolves to itself.
   */
  destinationFor(
    node: CanvasNode,
    allNodes: CanvasNode[],
    allEdges: { id: string; from: string; to: string }[],
  ): CanvasNode | null {
    let cursor: CanvasNode | undefined = node;
    let guard = 0;
    while (cursor && guard++ < 20) {
      if (isTransformNode(cursor) && cursor.transformId.startsWith('dest-')) return cursor;
      const next = allEdges.find(e => e.from === cursor!.id);
      cursor = next ? allNodes.find(n => n.id === next.to) : undefined;
    }
    return null;
  }

  /** Chain steps already present upstream of `destination`. */
  private chainStepsBefore(
    destination: CanvasNode,
    allNodes: CanvasNode[],
    allEdges: { id: string; from: string; to: string }[],
  ): string[] {
    const present: string[] = [];
    let cursor: CanvasNode | undefined = destination;
    let guard = 0;
    while (cursor && guard++ < 20) {
      const inbound = allEdges.find(e => e.to === cursor!.id);
      const predecessor = inbound ? allNodes.find(n => n.id === inbound.from) : undefined;
      if (!predecessor || !this.isChainStep(predecessor)) break;
      present.push((predecessor as TransformNode).transformId);
      cursor = predecessor;
    }
    return present;
  }

  /**
   * Which chain steps can still be added to `node`'s pipeline — every step not already in it. Mapping is
   * additionally gated on the destination being SQL-family (see SQL_FAMILY_DESTINATION_TYPES); the other
   * two apply to any destination. Order-independent by design: adding De-identification first still
   * leaves Transformation offerable afterwards, and vice versa.
   */
  /**
   * Whether this node's `+` has anything to offer — drives whether the button is shown at all, so the
   * user never opens the library only to find it empty (e.g. a destination whose pipeline already has
   * every applicable step). Same source of truth as the picker itself.
   */
  canAddNext(
    node: CanvasNode,
    allNodes: CanvasNode[],
    allEdges: { id: string; from: string; to: string }[],
  ): boolean {
    // De-identification is the last step in V2's chain (DEIDENTIFICATION_RANK — Source → Mapping →
    // Transformation → De-identification → Destination), so nothing can be added after it. The button reads
    // "Add next module", but addableChainSteps always inserts in canonical order, so the only step it could
    // still offer here (Transformation) would land UPSTREAM of this node — the button pointed at a position
    // nothing can go into and then inserted somewhere else. Add that step from any other node's `+` instead.
    if (isTransformNode(node) && node.transformId === 'deidentification') {
      return false;
    }

    const model = this.pickerModel(node, allNodes, allEdges);
    return model.items.length > 0 || !!model.mergeOpt;
  }

  private addableChainSteps(
    node: CanvasNode,
    allNodes: CanvasNode[],
    allEdges: { id: string; from: string; to: string }[],
  ): string[] {
    const destination = this.destinationFor(node, allNodes, allEdges);
    if (!destination) return [];
    const present = new Set(this.chainStepsBefore(destination, allNodes, allEdges));
    return ApplicabilityServiceV2.CHAIN_STEP_IDS.filter(id => {
      if (present.has(id)) return false;
      if (id === 'field-mapping') return this.isSqlFamilyDestination(destination);
      return true;
    });
  }

  // ── picker model — V2's straight chain, in canvas order:
  //   Source → [Mapping] → [Transformation] → [De-identification] → Destination
  // The source offers destinations. Every other node (the destination itself, or any chain step) offers
  // whichever chain steps this pipeline is still missing — they all get inserted between the source and
  // the destination in canonical order, so which node's `+` was used doesn't affect placement. Mapping is
  // offered only for SQL-family destinations; the other two always apply. Multi-source merge (picking 2+
  // EHR sources and merging them) is unrelated to this chain and preserved from the original picker. ────
  pickerModel(
    node: CanvasNode,
    allNodes: CanvasNode[],
    allEdges: { id: string; from: string; to: string }[],
  ): PickerModel {
    const hasChild = (nodeId: string): boolean => allEdges.some(e => e.from === nodeId);

    const cfg: EpicConfig = { context: '', ingestionMode: '', resources: [] };
    let mode: string;
    let rule: string;
    let ruleLabel: string;
    const attachTo: CanvasNode = node;
    let items: PickerItem[] = [];
    let mergeOpt: MergeNodeOption | null = null;

    if (isMergeNode(node)) {
      // Multi-source merge node — nothing follows it in V2's chain.
      mode = 'merge'; rule = 'Merge';
      ruleLabel = `Merge node — "${this.groupLabel(node.group)}" group merged.`;

    } else if (isSourceNode(node)) {
      mode = 'source'; rule = 'Source';
      ruleLabel = `Applicable next steps after "${this.nodeDisplayName(node)}".`;
      if (!hasChild(node.id)) {
        // No destination yet — that's the only thing that can follow a bare source.
        items = TRANSFORMS.filter(t => t.rank === DESTINATION_RANK).map(t => this.transformItem(t.id)!).filter(Boolean);
      } else {
        // A destination exists, so the source's `+` is where the first chain step goes — chain steps are
        // inserted between the source and the destination, which is exactly "after the source".
        items = this.addableChainSteps(node, allNodes, allEdges).map(id => this.transformItem(id)!).filter(Boolean);
      }
      // Multi-source merge: 2+ EHR source nodes not yet merged — unrelated to the Destination/Mapping/
      // Transformation/De-identification chain, preserved as-is from the original picker.
      const sources = allNodes.filter(n => !n.kind);
      const srcMergeExists = allNodes.some(n => n.kind === 'merge' && n.group === 'source');
      if (sources.length >= 2 && !srcMergeExists) {
        mergeOpt = { group: 'source', source: true, sourceIds: sources.map(s => s.id), count: sources.length };
      }

    } else if (isTransformNode(node)) {
      if (node.transformId.startsWith('dest-')) {
        // The destination TERMINATES the pipeline — nothing is ever added after it, so it offers nothing
        // and its `+` is hidden entirely (see canAddNext). Chain steps are added from the source or from
        // another chain step, and always land in front of this node.
        mode = 'destination'; rule = 'Destination';
        ruleLabel = `"${this.nodeDisplayName(node)}" is the end of this pipeline.`;
      } else {
        // Chain step — offers whichever chain steps this pipeline is still missing. Which node's `+` was
        // used doesn't affect placement (see WorkflowBuilderV2Component.insertChainStep).
        mode = 'chain-step'; rule = 'Chain step';
        items = this.addableChainSteps(node, allNodes, allEdges).map(id => this.transformItem(id)!).filter(Boolean);
        ruleLabel = items.length
          ? `Steps that can still be added between the source and "${this.nodeDisplayName(this.destinationFor(node, allNodes, allEdges) ?? node)}".`
          : `Every applicable step is already in this pipeline.`;
      }

    } else {
      mode = 'forward'; rule = 'Forward';
      ruleLabel = `Applicable next steps after "${this.nodeDisplayName(node)}".`;
    }

    return { mode, rule, attachTo, ruleLabel: ruleLabel!, items, mergeOpt, cfg, node };
  }
}
