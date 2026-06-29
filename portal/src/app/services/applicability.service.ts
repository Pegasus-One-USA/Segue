import { Injectable } from '@angular/core';
import { TRANSFORMS, REPEATABLE_TRANSFORMS } from '../data/transforms.data';
import { REASON, CODED_RESOURCES } from '../data/scope-constants.data';
import { RANK_LABEL } from '../models/transform.model';
import {
  ApplicabilityResult,
  ApplicabilityOrigin,
  EpicConfig,
  ApplicabilityStatus,
} from '../models/transform-applicability.model';
import { CanvasNode, isSourceNode, isMergeNode, isTransformNode } from '../models/node.model';
import { PickerModel, PickerItem, MergeNodeOption } from '../models/wizard-state.model';

export const SOURCE_RANK = 0;

const RANK_SEQUENCE = 'Source(0)→Consent(1)→Validation(2)→Normalize(3)→Terminology(4)→De-identify(5)→Map(6)→Destination(7)→Audit(8)→Analytics(9)';

@Injectable({ providedIn: 'root' })
export class ApplicabilityService {

  // ── core applicability ─────────────────────────────────────────────────────
  applicableTransforms(
    cfg: EpicConfig,
    origin?: ApplicabilityOrigin
  ): ApplicabilityResult[] {
    const ctx    = cfg.context ?? '';
    const mode   = cfg.ingestionMode ?? '';
    const res    = cfg.resources ?? [];
    const originRank = origin?.rank ?? SOURCE_RANK;
    const existing   = origin?.existingTransformIds ?? [];

    const isStream        = mode === 'subscription' || mode === 'webhook';
    const isIncremental   = mode === 'search';
    const isSinglePatient = ctx === 'Patient (standalone)';
    const isProvider      = ctx === 'Provider (EHR launch)' || ctx === 'Provider (standalone)';
    const hasCodedRes     = res.some(r => CODED_RESOURCES.includes(r));
    const hasPatientRes   = res.includes('Patient');

    const show   = (): Pick<ApplicabilityResult, 'status' | 'reason' | 'code'> =>
      ({ status: 'show',   reason: null, code: null });
    const caveat = (code: string): Pick<ApplicabilityResult, 'status' | 'reason' | 'code'> =>
      ({ status: 'caveat', reason: REASON[code] ?? code, code });
    const hide   = (code: string): Pick<ApplicabilityResult, 'status' | 'reason' | 'code'> =>
      ({ status: 'hide',   reason: REASON[code] ?? code, code });

    const ruleFor = (id: string) => {
      switch (id) {
        case 'fhir-validation':
        case 'normalize':
        case 'field-mapping':
          return show();

        case 'terminology':
          return hasCodedRes ? show() : caveat('NO_CODED_RESOURCES');

        case 'patient-matching':
        case 'merge-patients':
          if (isStream)        return hide('STREAM_SINGLE_RESOURCE');
          if (isSinglePatient) return hide('SINGLE_PATIENT_NO_COHORT');
          if (!hasPatientRes)  return hide('NO_PATIENT_RESOURCE');
          if (isProvider)      return caveat('PROVIDER_PARTIAL');
          return show();

        case 'deid-safeharbor':
          return isSinglePatient ? caveat('DEID_UNUSUAL_PATIENT') : show();

        case 'deid-kanon':
          if (isStream)        return hide('STREAM_SINGLE_RESOURCE');
          if (isSinglePatient) return hide('SINGLE_PATIENT_NO_COHORT');
          if (isIncremental)   return caveat('NEEDS_FULL_DATASET');
          if (isProvider)      return caveat('NEEDS_FULL_DATASET');
          return show();

        default:
          return show();
      }
    };

    return TRANSFORMS.map(t => {
      let r: Pick<ApplicabilityResult, 'status' | 'reason' | 'code'>;
      const isSingle = t.rank < 7 && !REPEATABLE_TRANSFORMS.has(t.id);

      if (t.rank <= originRank) {
        r = hide('BACKWARD_RANK');
      } else if (isSingle && existing.includes(t.id)) {
        r = hide('ALREADY_IN_CHAIN');
      } else {
        r = ruleFor(t.id);
      }

      return {
        id:     t.id,
        name:   t.name,
        sub:    t.sub,
        rank:   t.rank,
        group:  t.group ?? null,
        status: r.status,
        reason: r.reason,
        code:   r.code,
      };
    });
  }

  // ── group helpers ──────────────────────────────────────────────────────────
  groupOf(transformId: string): string | null {
    const t = TRANSFORMS.find(x => x.id === transformId);
    return t?.group ?? null;
  }

  groupMembers(group: string): string[] {
    return TRANSFORMS.filter(t => t.group === group).map(t => t.id);
  }

  groupLabel(group: string): string {
    if (group === 'source') return 'Source';
    const t = TRANSFORMS.find(x => x.group === group);
    return t ? (RANK_LABEL[t.rank] ?? group) : group;
  }

  groupRank(group: string): number {
    if (group === 'source') return SOURCE_RANK;
    const t = TRANSFORMS.find(x => x.group === group);
    return t?.rank ?? 0;
  }

  nodeRankFromStore(node: CanvasNode): number {
    if (isMergeNode(node))     return this.groupRank(node.group);
    if (isTransformNode(node)) return TRANSFORMS.find(t => t.id === node.transformId)?.rank ?? 6;
    return SOURCE_RANK;
  }

  nodeDisplayName(node: CanvasNode | undefined): string {
    if (!node) return '?';
    if (isMergeNode(node))     return 'Merge · ' + this.groupLabel(node.group);
    if (isTransformNode(node)) return TRANSFORMS.find(t => t.id === node.transformId)?.name ?? 'transform';
    return node.fields['__name'] ?? 'Epic';
  }

  // ── picker model (6 group visibility rules) ────────────────────────────────
  pickerModel(
    node: CanvasNode,
    allNodes: CanvasNode[],
    allEdges: Array<{ id: string; from: string; to: string }>,
    showHidden: boolean
  ): PickerModel {
    const byId = (id: string) => allNodes.find(n => n.id === id);
    const directTransformChildren = (nodeId: string): CanvasNode[] =>
      allEdges.filter(e => e.from === nodeId)
        .map(e => byId(e.to))
        .filter((n): n is CanvasNode => !!n && n.kind === 'transform');

    const parentOf = (nodeId: string): CanvasNode | undefined => {
      const inbound = allEdges.find(e => e.to === nodeId);
      return inbound ? byId(inbound.from) : undefined;
    };

    const assembledGroup = (nodeId: string): { group: string; added: string[] } | null => {
      const counts: Record<string, string[]> = {};
      directTransformChildren(nodeId).forEach(k => {
        if (!isTransformNode(k)) return;
        const g = this.groupOf(k.transformId);
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
    };

    const rootOf = (n: CanvasNode): CanvasNode | undefined => {
      let cur: CanvasNode | undefined = n;
      let guard = 0;
      while (cur && (cur.kind === 'transform' || cur.kind === 'merge') && guard++ < 50) {
        const inbound = allEdges.find(e => e.to === cur!.id);
        cur = inbound ? byId(inbound.from) : undefined;
      }
      return cur && !cur.kind ? cur : undefined;
    };

    const existingTransforms = (originNode: CanvasNode): string[] => {
      const ids = new Set<string>();
      if (isTransformNode(originNode)) ids.add(originNode.transformId);
      const seen = new Set<string>();
      const visit = (id: string): void => {
        if (seen.has(id)) return;
        seen.add(id);
        allEdges.filter(e => e.from === id).forEach(e => {
          const n = byId(e.to);
          if (!n) return;
          if (isTransformNode(n)) ids.add(n.transformId);
          visit(n.id);
        });
      };
      visit(originNode.id);
      return [...ids];
    };

    const root = rootOf(node) ?? node;
    const cfg: EpicConfig = {
      context:       root.fields?.['App context'] ?? '',
      ingestionMode: root.fields?.['Ingestion mode'] ?? '',
      resources:     (root.fields?.['Resources'] ?? '').split(',').map((s: string) => s.trim()).filter(Boolean),
    };

    const originRank  = this.nodeRankFromStore(node);
    const existing    = existingTransforms(node);
    const myGroup     = isTransformNode(node) ? this.groupOf(node.transformId) : null;
    const parent      = parentOf(node.id);
    const parentGrp   = parent ? assembledGroup(parent.id) : null;
    const ownGrp      = assembledGroup(node.id);

    const rulingMap = (rank: number): Record<string, ApplicabilityResult> => {
      const map: Record<string, ApplicabilityResult> = {};
      this.applicableTransforms(cfg, { rank, existingTransformIds: existing })
          .forEach(r => { map[r.id] = r; });
      return map;
    };

    const decorate = (
      t: { id: string; name: string; sub: string; rank: number; group?: string },
      r?: ApplicabilityResult
    ): PickerItem => ({
      id:     t.id,
      name:   t.name,
      sub:    t.sub,
      rank:   t.rank,
      group:  t.group ?? null,
      status: r?.status ?? 'show',
      reason: r?.reason ?? null,
    });

    let mode: string;
    let rule: string;
    let ruleLabel: string;
    let attachTo: CanvasNode = node;
    let items: PickerItem[] = [];
    let mergeOpt: MergeNodeOption | null = null;

    if (isMergeNode(node)) {
      // Rule 6 — forward only from merge node
      mode = 'merge'; rule = 'Rule 6';
      ruleLabel = `Merge node — "${this.groupLabel(node.group)}" group merged. Forward-rank steps only; group members never reappear.`;
      const rm = rulingMap(this.nodeRankFromStore(node));
      items = TRANSFORMS
        .filter(t => t.rank > this.nodeRankFromStore(node) && t.group !== node.group)
        .map(t => decorate(t, rm[t.id]));

    } else if (myGroup && parentGrp && parentGrp.group === myGroup && parentGrp.added.length >= 2) {
      // Rule 4 — group child with 2+ siblings
      mode = 'group-child'; attachTo = parent!; rule = 'Rule 4';
      ruleLabel = `Member of the "${this.groupLabel(myGroup)}" group (${parentGrp.added.length} added). Add another member, or merge them.`;
      const rm = rulingMap(this.nodeRankFromStore(parent!));
      const remaining = this.groupMembers(myGroup).filter(id => !parentGrp.added.includes(id));
      remaining.forEach(id => {
        const t = TRANSFORMS.find(x => x.id === id);
        if (t) items.push(decorate(t, rm[id]));
      });
      const kids = directTransformChildren(parent!.id)
        .filter(k => isTransformNode(k) && this.groupOf(k.transformId) === myGroup);
      const alreadyMerged = kids.some(k =>
        allEdges.some(e => e.from === k.id && byId(e.to)?.kind === 'merge')
      );
      if (!alreadyMerged) {
        mergeOpt = { group: myGroup, parentId: parent!.id, count: parentGrp.added.length };
      }

    } else if (ownGrp) {
      // Rules 1–3 unified — parent mid-assembling a group
      const remaining = this.groupMembers(ownGrp.group).filter(id => !ownGrp.added.includes(id));
      mode = 'group-parent'; rule = ownGrp.added.length >= 2 ? 'Rule 3' : 'Rule 2';
      ruleLabel = remaining.length
        ? `"${this.groupLabel(ownGrp.group)}" group — ${ownGrp.added.length} added. Only remaining group members can be added here.`
        : `All "${this.groupLabel(ownGrp.group)}" members added. Open ⊕ on a member to merge & move forward.`;
      const rm = rulingMap(originRank);
      remaining.forEach(id => {
        const t = TRANSFORMS.find(x => x.id === id);
        if (t) items.push(decorate(t, rm[id]));
      });

    } else {
      // Default forward picker
      mode = 'forward'; rule = 'Forward';
      ruleLabel = `Applicable next steps after "${this.nodeDisplayName(node)}".`;
      const rm = rulingMap(originRank);
      items = TRANSFORMS.map(t => decorate(t, rm[t.id]));

      if (!node.kind) {
        const sources = allNodes.filter(n => !n.kind);
        const srcMergeExists = allNodes.some(n => n.kind === 'merge' && n.group === 'source');
        if (sources.length >= 2 && !srcMergeExists) {
          mergeOpt = { group: 'source', source: true, sourceIds: sources.map(s => s.id), count: sources.length };
        }
      }
    }

    items = items.filter(it => it.status !== 'hide' || showHidden);

    return { mode, rule, attachTo, ruleLabel, items, mergeOpt, cfg, node };
  }
}
