import { Injectable, inject } from '@angular/core';
import { PipelineStoreV2 } from './pipeline-v2.store';
import { CanvasNode, isTransformNode, isMergeNode } from '../models/node-v2.model';
import { TRANSFORMS } from '../data/transforms-v2.data';
import { PipelinePayload, PayloadEntry } from '../models/pipeline-payload-v2.model';
import { ApplicabilityServiceV2 } from './applicability-v2.service';

@Injectable({ providedIn: 'root' })
export class PayloadServiceV2 {
  private readonly store      = inject(PipelineStoreV2);
  private readonly appService = inject(ApplicabilityServiceV2);

  build(scenarioName: string): PipelinePayload {
    const nodes = this.store.nodes();
    const edges = this.store.edges();

    const nodeName = (id: string): string => {
      const n = this.store.byId(id);
      if (!n) return '?';
      if (isMergeNode(n))     return n.fields['__name'] ?? 'Merge';
      if (isTransformNode(n)) return TRANSFORMS.find(t => t.id === n.transformId)?.name ?? n.transformId;
      return n.fields['__name'] ?? 'Unknown';
    };

    const entries: PayloadEntry[] = nodes.map((n, i) => {
      if (isTransformNode(n)) {
        const t = TRANSFORMS.find(x => x.id === n.transformId);
        return {
          ref:          i + 1,
          stage:        'transform' as const,
          rank:         t?.rank ?? 0,
          group:        t?.group ?? null,
          transform:    t?.name ?? n.transformId,
          appliedFrom:  n.sourceName,
          applicability: n.statusAtAdd,
        };
      }
      if (isMergeNode(n)) {
        return {
          ref:         i + 1,
          stage:       'merge' as const,
          group:       n.group,
          rank:        this.appService.groupRank(n.group),
          mergesInputs: edges.filter(e => e.to === n.id).map(e => nodeName(e.from)),
        };
      }
      return {
        ref:       i + 1,
        stage:     'source' as const,
        connector: n.connectorLabel ?? 'Epic',
        connected: !!(n as any).connected,
        config:    n.fields,
      };
    });

    return {
      scenario: scenarioName,
      tenant:   'Acme Health',
      nodes:    entries,
      edges:    edges.map(e => ({ from: nodeName(e.from), to: nodeName(e.to) })),
    };
  }
}
