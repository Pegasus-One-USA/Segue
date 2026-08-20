import { Injectable, inject } from '@angular/core';
import { TRANSFORMS } from '../data/transforms.data';
import { NodeCatalogService } from './node-catalog.service';
import { SOURCE_ID_TO_TYPE, DESTINATION_ID_TO_TYPE } from '../data/node-catalog-legacy-ids';
import { sourceFormKeyForNode } from '../components/node-library/source-node-vendor.util';
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
  'generic-fhir': 'GenericFhirSourceNode',
  'fhir-validation': 'UsCoreValidationNode',
  normalize: 'NormalizationNode',
  'patient-matching': 'PatientMatchingNode',
  'merge-patients': 'PatientMatchingNode',
  terminology: 'TerminologyNode',
  'deid-safeharbor': 'DeIdentificationNode',
  'deid-kanon': 'DeIdentificationNode',
  'field-mapping': 'MappingNode',
  'dest-sqlserver': 'SqlServerDestinationNode',
  'dest-mysql': 'MySqlDestinationNode',
  'dest-postgres': 'PostgreSqlDestinationNode',
  'dest-mongo': 'MongoDestinationNode',
  'dest-medplum': 'MedplumDestinationNode',
  'dest-fhir': 'FhirRepositoryDestinationNode',
  'dest-blob': 'BlobDestinationNode',
  'dest-csv': 'CsvDestinationNode',
  'audit-lineage': 'AuditLineageNode',
  hedis: 'HedisMeasureReportNode',
  anomaly: 'AnomalyDetectionNode',
  'patient-agg': 'PatientAggregationNode',
};

// Credential fields captured by the wizards for connection-secret assembly. They are redacted from the persisted
// graph so plaintext secrets never land in WorkflowNodes.ConfigurationJson; create-on-save reads them straight from
// the in-memory store to build the encrypted inlineSecret instead.
const SECRET_FIELD_KEYS = new Set([
  'dest_password', 'dest_sftpPassword', 'dest_connectionString', 'dest_clientSecret', 'dest_bearerToken',
  'dest_blobSecret', 'dest_medplumSecret', 'Client Secret',
]);

@Injectable({ providedIn: 'root' })
export class WorkflowGraphMapperService {
  private readonly store = inject(PipelineStore);
  private readonly workflowApi = inject(WorkflowApiService);
  private readonly nodeCatalog = inject(NodeCatalogService);

  constructor() {
    this.nodeCatalog.ensureLoaded();
  }

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

      if (this.isDestination(to) && !this.isMapping(from) && !this.isFhirDirectDestination(to)) {
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

    // Stamp each mapping node with the destinationId of the destination it feeds. The runtime MappingNode
    // resolves the destination's type from this id; whole-resource FHIR destinations (Medplum, FHIR Repository)
    // need it to emit one SourceJson carrier record per resource — without it the mapping silently drops every
    // record and the run "succeeds" having written nothing. The value is read from the destination node's own
    // fields (set by the wizard as soon as the connection is configured — for new and existing connections
    // alike), following the graph edge mapping->destination. It is never a literal id: an explicit mapping node
    // the user placed on the canvas is serialized by nodeToRequest, which only keeps that node's own fields, so
    // the linkage the graph already expresses has to be re-applied here. (The syntheticMappingRequest path, used
    // when a source connects straight to a destination, already copies the whole destination field bag.)
    for (const edge of emittedEdges) {
      const destinationNode = byId.get(edge.toNodeId);
      const mappingRequest = requests.find(request => request.id === edge.fromNodeId);
      if (!destinationNode || !this.isDestination(destinationNode) || !mappingRequest) continue;
      if (mappingRequest.nodeType !== FALLBACK_NODE_TYPES['field-mapping']) continue;

      const destinationId = destinationNode.fields['destinationId'];
      if (!destinationId) continue;

      const config = this.parseConfig(mappingRequest.configurationJson);
      if (config['destinationId']) continue;
      mappingRequest.configurationJson = JSON.stringify({ ...config, destinationId });
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
      ? (this._destCatalogEntry(node.transformId) ? 7 : TRANSFORMS.find(transform => transform.id === node.transformId)?.rank ?? 60)
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
      const sourceType = SOURCE_ID_TO_TYPE[transformId];
      const source = sourceType ? this.nodeCatalog.find('Source', sourceType) : undefined;
      return {
        id: node.id,
        kind: undefined,
        x: node.positionX,
        y: node.positionY,
        connected: true,
        abbr: source?.icon ?? name.slice(0, 2).toUpperCase(),
        color: source?.color ?? undefined,
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
    // Canonical resolution, not a display-name guess: sourceFormKeyForNode() is the exact same
    // resolver canvas.component.ts's node-delete permission gate and the Node Library's edit-reopen
    // flow already use, so a source node's vendor is never derived two different ways. It reads the
    // 'Connector' value every vendor form's own getFields() writes (via EHR_VENDOR_TO_SOURCE_FORM_KEY)
    // — covering all nine source types, not just sample/generic-fhir — and only falls back to 'epic'
    // for a node saved before the Connector field existed, preserving prior behavior for that one
    // legacy case exactly. Previously this duplicated a narrower 2-pattern regex here that silently
    // mislabeled Cerner/Athenahealth/Allscripts/Healow/Meditech/HL7v2 source nodes as 'epic' on save.
    return sourceFormKeyForNode(node);
  }

  private transformIdFromNodeType(nodeType: string): string {
    const found = Object.entries(FALLBACK_NODE_TYPES).find(([, value]) => value === nodeType);
    return found?.[0] ?? nodeType;
  }

  private displayNameFor(node: CanvasNode, item?: WorkflowCatalogItem): string {
    if (node.fields['__name']) return node.fields['__name'];
    if (item?.displayName) return item.displayName;
    if (node.kind === 'transform') {
      return this._destCatalogEntry(node.transformId)?.displayName
        ?? TRANSFORMS.find(transform => transform.id === node.transformId)?.name
        ?? node.transformId;
    }
    if (node.kind === 'merge') return node.fields['__name'] ?? 'Merge';
    return node.connectorLabel ?? 'Epic';
  }

  private _destCatalogEntry(transformId: string) {
    const type = DESTINATION_ID_TO_TYPE[transformId];
    return type ? this.nodeCatalog.find('Destination', type) : undefined;
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

  // Mirrors workflow-builder.component.ts's resolveDestinationAttachPoint() skip-insertion logic: a
  // FhirRepositoryDestination is exempt from WorkflowGraphValidator's upstream-Mapping-node requirement
  // unconditionally (both "passthrough" and "customize" modes — both are handled directly by
  // MappingNodeExecutor/FhirFieldTransformApplier against the raw resource batch, neither needs a real Mapping
  // node's field-mapping engine), so a direct source/transform -> destination edge should persist as-is rather
  // than getting a synthetic Mapping node inserted to satisfy a requirement that doesn't apply to this node type.
  private isFhirDirectDestination(node: CanvasNode): boolean {
    return node.kind === 'transform' && node.transformId === 'dest-fhir';
  }

  private isSourceCategory(category: WorkflowNodeCategory | string): boolean {
    return category === CATEGORY_SOURCE || category === 'Source';
  }
}
