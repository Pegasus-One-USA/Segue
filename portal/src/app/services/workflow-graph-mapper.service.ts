import { Injectable, inject } from '@angular/core';
import { TRANSFORMS } from '../data/transforms.data';
import { SOURCES } from '../data/sources.data';
import { CanvasEdge } from '../models/edge.model';
import { CanvasNode, SourceNode, TransformNode } from '../models/node.model';
import { PipelineStore } from './pipeline.store';
import {
  WorkflowApiService,
  WorkflowCatalogItem,
  WorkflowDefinitionDto,
  WorkflowDefinitionRequest,
  WorkflowTriggerRequest,
  WorkflowEdgeRequest,
  WorkflowNodeCategory,
  WorkflowNodeRequest,
} from './workflow-api.service';

const CATEGORY_SOURCE: WorkflowNodeCategory = 0;
const CATEGORY_TRANSFORM: WorkflowNodeCategory = 10;

const FALLBACK_NODE_TYPES: Record<string, string> = {
  epic: 'EpicSourceNode',
  sample: 'SampleSourceNode',
  'fhir-validation': 'UsCoreValidationNode',
  normalize: 'NormalizationNode',
  'patient-matching': 'PatientMatchingNode',
  'merge-patients': 'PatientMatchingNode',
  terminology: 'TerminologyNode',
  'deid-safeharbor': 'DeIdentificationNode',
  'deid-kanon': 'DeIdentificationNode',
  'field-mapping': 'MappingNode',
  'dest-sqlserver': 'SqlServerDestinationNode',
  'dest-csv': 'CsvDestinationNode',
  'audit-lineage': 'AuditLineageNode',
  hedis: 'HedisMeasureReportNode',
  anomaly: 'AnomalyDetectionNode',
  'patient-agg': 'PatientAggregationNode',
};

// Credential fields captured by the wizards for connection-secret assembly. They are redacted from the persisted
// graph so plaintext secrets never land in WorkflowNodes.ConfigurationJson; create-on-save reads them straight from
// the in-memory store to build the encrypted inlineSecret instead.
const SECRET_FIELD_KEYS = new Set(['dest_password', 'dest_sftpPassword']);

@Injectable({ providedIn: 'root' })
export class WorkflowGraphMapperService {
  private readonly store = inject(PipelineStore);
  private readonly workflowApi = inject(WorkflowApiService);

  toRequest(name: string, trigger?: WorkflowTriggerRequest | null): WorkflowDefinitionRequest {
    const catalog = this.workflowApi.catalog();
    const nodes = this.store.nodes();
    const edges = this.store.edges();
    const requests: WorkflowNodeRequest[] = [];
    const requestIds = new Set<string>();

    for (const node of nodes) {
      if (node.kind === 'merge') continue;
      const request = this.nodeToRequest(node, catalog);
      requests.push(request);
      requestIds.add(request.id);
    }

    const emittedEdges: WorkflowEdgeRequest[] = [];
    const addEdge = (fromNodeId: string, toNodeId: string): void => {
      if (!requestIds.has(fromNodeId) || !requestIds.has(toNodeId)) return;
      if (emittedEdges.some(edge => edge.fromNodeId === fromNodeId && edge.toNodeId === toNodeId)) return;
      emittedEdges.push({ fromNodeId, toNodeId });
    };

    const byId = new Map(nodes.map(node => [node.id, node]));
    for (const edge of edges) {
      const from = byId.get(edge.from);
      const to = byId.get(edge.to);
      if (!from || !to) continue;

      if (from.kind === 'merge') {
        for (const incoming of edges.filter(candidate => candidate.to === from.id)) {
          addEdge(incoming.from, to.id);
        }
        continue;
      }

      if (to.kind === 'merge') continue;

      if (this.isDestination(to) && !this.isMapping(from)) {
        const mappingId = `${to.id}__mapping`;
        if (!requestIds.has(mappingId)) {
          requests.push(this.syntheticMappingRequest(mappingId, from, to, catalog));
          requestIds.add(mappingId);
        }
        addEdge(from.id, mappingId);
        addEdge(mappingId, to.id);
        continue;
      }

      addEdge(from.id, to.id);
    }

    return {
      name,
      isEnabled: true,
      nodes: requests,
      edges: emittedEdges,
      trigger: trigger ?? null,
    };
  }

  loadDefinition(definition: WorkflowDefinitionDto): void {
    const catalog = this.workflowApi.catalog();
    const nodes: CanvasNode[] = definition.nodes.map(node => this.nodeFromDto(node, catalog));
    const edges: CanvasEdge[] = definition.edges.map((edge, index) => ({
      id: edge.id || `e${index}`,
      from: edge.fromNodeId,
      to: edge.toNodeId,
    }));
    this.store.loadGraph(nodes, edges);
  }

  findLaunchSourceId(): string | null {
    const source = this.store.nodes().find(node => !node.kind);
    if (!source) return null;
    return source.fields['Source connection id']
      ?? source.fields['SourceConnectionId']
      ?? source.fields['sourceConnectionId']
      ?? source.fields['source_connection_id']
      ?? null;
  }

  private nodeToRequest(node: CanvasNode, catalog: WorkflowCatalogItem[]): WorkflowNodeRequest {
    const transformId = this.transformIdForNode(node);
    const item = this.catalogForTransform(transformId, catalog);
    const fallbackRank = node.kind === 'transform'
      ? TRANSFORMS.find(transform => transform.id === node.transformId)?.rank ?? 60
      : 0;

    return {
      id: node.id,
      nodeType: item?.nodeType ?? FALLBACK_NODE_TYPES[transformId] ?? transformId,
      category: item?.category ?? (node.kind ? CATEGORY_TRANSFORM : CATEGORY_SOURCE),
      rank: item?.rank ?? fallbackRank,
      subRank: 0,
      displayName: this.displayNameFor(node, item),
      configurationJson: JSON.stringify({
        ...this.redactSecrets(node.fields),
        __transformId: transformId,
        __name: node.fields['__name'] ?? item?.displayName ?? this.displayNameFor(node, item),
      }),
      positionX: node.x,
      positionY: node.y,
      isEnabled: true,
      checkpointUrlEnabled: !!node.checkpointUrlEnabled,
    };
  }

  private syntheticMappingRequest(
    mappingId: string,
    from: CanvasNode,
    destination: CanvasNode,
    catalog: WorkflowCatalogItem[],
  ): WorkflowNodeRequest {
    const item = this.catalogForTransform('field-mapping', catalog);
    const config = {
      ...this.redactSecrets(destination.fields),
      __transformId: 'field-mapping',
      __name: item?.displayName ?? 'Field Mapping',
      destinationTransformId: this.transformIdForNode(destination),
    };
    return {
      id: mappingId,
      nodeType: item?.nodeType ?? FALLBACK_NODE_TYPES['field-mapping'],
      category: item?.category ?? CATEGORY_TRANSFORM,
      rank: item?.rank ?? 60,
      subRank: 0,
      displayName: item?.displayName ?? 'Field Mapping',
      configurationJson: JSON.stringify(config),
      positionX: Math.max(from.x + 120, destination.x - 150),
      positionY: destination.y,
      isEnabled: true,
    };
  }

  private nodeFromDto(node: WorkflowNodeRequest, catalog: WorkflowCatalogItem[]): CanvasNode {
    const config = this.parseConfig(node.configurationJson);
    const item = catalog.find(candidate => candidate.nodeType === node.nodeType);
    const transformId = config['__transformId'] ?? item?.transformId ?? this.transformIdFromNodeType(node.nodeType);
    const name = config['__name'] ?? node.displayName ?? item?.displayName ?? transformId;

    if (this.isSourceCategory(node.category)) {
      const source = SOURCES.find(candidate => candidate.id === transformId);
      return {
        id: node.id,
        kind: undefined,
        x: node.positionX,
        y: node.positionY,
        connected: true,
        abbr: source?.abbr ?? name.slice(0, 2).toUpperCase(),
        color: source?.color,
        connectorLabel: name,
        fields: { ...config, __name: name },
        checkpointUrlEnabled: !!node.checkpointUrlEnabled,
      } satisfies SourceNode;
    }

    return {
      id: node.id,
      kind: 'transform',
      transformId,
      sourceName: '',
      statusAtAdd: 'show',
      x: node.positionX,
      y: node.positionY,
      fields: { ...config, __name: name },
      checkpointUrlEnabled: !!node.checkpointUrlEnabled,
    } satisfies TransformNode;
  }

  private catalogForTransform(transformId: string, catalog: WorkflowCatalogItem[]): WorkflowCatalogItem | undefined {
    return catalog.find(item => item.transformId === transformId)
      ?? catalog.find(item => item.nodeType === FALLBACK_NODE_TYPES[transformId]);
  }

  private transformIdForNode(node: CanvasNode): string {
    if (node.kind === 'transform') return node.transformId;
    if (node.kind === 'merge') return 'merge';
    const connector = node.fields['Connector'] ?? node.connectorLabel ?? node.fields['__name'] ?? '';
    if (/sample/i.test(connector)) return 'sample';
    return 'epic';
  }

  private transformIdFromNodeType(nodeType: string): string {
    const found = Object.entries(FALLBACK_NODE_TYPES).find(([, value]) => value === nodeType);
    return found?.[0] ?? nodeType;
  }

  private displayNameFor(node: CanvasNode, item?: WorkflowCatalogItem): string {
    if (node.fields['__name']) return node.fields['__name'];
    if (item?.displayName) return item.displayName;
    if (node.kind === 'transform') {
      return TRANSFORMS.find(transform => transform.id === node.transformId)?.name ?? node.transformId;
    }
    if (node.kind === 'merge') return node.fields['__name'] ?? 'Merge';
    return node.connectorLabel ?? 'Epic';
  }

  private redactSecrets(fields: Record<string, string>): Record<string, string> {
    return Object.fromEntries(Object.entries(fields).filter(([key]) => !SECRET_FIELD_KEYS.has(key)));
  }

  private parseConfig(json: string | null): Record<string, string> {
    if (!json) return {};
    try {
      const parsed = JSON.parse(json) as Record<string, unknown>;
      return Object.fromEntries(
        Object.entries(parsed).map(([key, value]) => [
          key,
          typeof value === 'string' ? value : JSON.stringify(value),
        ]),
      );
    } catch {
      return {};
    }
  }

  private isDestination(node: CanvasNode): boolean {
    return node.kind === 'transform' && node.transformId.startsWith('dest-');
  }

  private isMapping(node: CanvasNode): boolean {
    return node.kind === 'transform' && node.transformId === 'field-mapping';
  }

  private isSourceCategory(category: WorkflowNodeCategory | string): boolean {
    return category === CATEGORY_SOURCE || category === 'Source';
  }
}
