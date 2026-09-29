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

// Vendor ids whose own source NodeType arrived WITH per-vendor source nodes, so a backend older than that
// change has no catalog entry for them. nodeToRequest downgrades these to the shared Epic source node when the
// running API's catalog doesn't list them, so a portal deployed ahead of its API can't save a workflow that
// WorkflowGraphValidator then rejects at RUN time.
const CATALOG_GUARDED_VENDOR_TRANSFORM_IDS = new Set(['athena', 'healow']);

// Vendor ids (SOURCES, sources.data.ts) that have a source NodeType of their own in the backend catalog.
// Anything outside this set — Cerner, Allscripts, Meditech, HL7 v2 — must still save as 'epic':
// WorkflowGraphValidator rejects at RUN time any NodeType missing from DefaultWorkflowNodeCatalog.Items, and
// those vendors are gated out of it until each has a registered IFhirSourceClient. Keep in step with that file.
const VENDOR_SOURCE_TRANSFORM_IDS = new Set(['epic', 'athena', 'healow', 'generic-fhir', 'sample']);

const FALLBACK_NODE_TYPES: Record<string, string> = {
  epic: 'EpicSourceNode',
  athena: 'AthenahealthSourceNode',
  healow: 'EClinicalWorksSourceNode',
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
  'dest-azurefhir': 'AzureFhirServiceDestinationNode',
  'dest-blob': 'BlobDestinationNode',
  'dest-datalake-webhook': 'DataLakeWebhookDestinationNode',
  'dest-apiendpoint': 'ApiEndpointDestinationNode',
  'dest-fabric': 'DataFabricAzureDestinationNode',
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
  // Lake destinations: the webhook credential (bearer token / API key / HMAC secret / OAuth2 client
  // secret) and the Fabric service-principal client secret. Both live only in the provisioned Key
  // Vault entry — never on the node.
  'dest_dlwSecret', 'dest_fabricSecret',
  // API Endpoint's single secret control — same "never on the node" rule as the two above.
  'dest_apiSecret',
]);

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
    const transformId = this.catalogSupportedTransformId(this.transformIdForNode(node), node, catalog);
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
        // Cosmetic-only vendor hint (see SourceNode.vendorId's doc comment) — never fed into nodeType/
        // transformId, so it can't affect what NodeType the backend validates this node against. Only
        // ever set on a source node (TransformNode/MergeNode have no vendorId field to begin with).
        ...((node as SourceNode).vendorId ? { __vendorId: (node as SourceNode).vendorId! } : {}),
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
      // transformId is still not enough on its own for display: a vendor with no NodeType of its own
      // (Cerner/Allscripts/Meditech — see VENDOR_SOURCE_TRANSFORM_IDS) saves under the shared
      // 'EpicSourceNode'/'epic' transformId, as does every node saved before per-vendor node types existed,
      // so matching SOURCES by transformId alone would mislabel those as Epic. __vendorId (see nodeToRequest)
      // is the reliable source for a node saved since it was added; guessVendorId's name-match is the
      // best-effort fallback for anything older.
      const vendorId = config['__vendorId'] ?? this.guessVendorId(name) ?? transformId;
      const source = SOURCES.find(candidate => candidate.id === vendorId);
      return {
        id: node.id,
        kind: undefined,
        x: node.positionX,
        y: node.positionY,
        connected: true,
        abbr: source?.abbr ?? name.slice(0, 2).toUpperCase(),
        color: source?.color,
        connectorLabel: name,
        vendorId: source?.id,
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

  /** Downgrades a vendor source node to the shared 'epic' node type when the running API's catalog has no entry
   *  for that vendor (see CATALOG_GUARDED_VENDOR_TRANSFORM_IDS). An empty catalog means "not loaded yet / the
   *  request failed", which is NOT the same as "this vendor is unsupported", so it is left alone — the existing
   *  FALLBACK_NODE_TYPES path already covers that case. */
  private catalogSupportedTransformId(
    transformId: string,
    node: CanvasNode,
    catalog: WorkflowCatalogItem[],
  ): string {
    if (node.kind || catalog.length === 0) return transformId;
    if (!CATALOG_GUARDED_VENDOR_TRANSFORM_IDS.has(transformId)) return transformId;
    return catalog.some(item => item.transformId === transformId) ? transformId : 'epic';
  }

  private catalogForTransform(transformId: string, catalog: WorkflowCatalogItem[]): WorkflowCatalogItem | undefined {
    return catalog.find(item => item.transformId === transformId)
      ?? catalog.find(item => item.nodeType === FALLBACK_NODE_TYPES[transformId]);
  }

  private transformIdForNode(node: CanvasNode): string {
    if (node.kind === 'transform') return node.transformId;
    if (node.kind === 'merge') return 'merge';
    const connector = node.fields['Connector'] ?? node.connectorLabel ?? node.fields['__name'] ?? '';
    // The vendor the canvas/wizard actually recorded on this node comes first, so a saved node carries its own
    // vendor NodeType (AthenahealthSourceNode, EClinicalWorksSourceNode, ...) instead of every EHR sharing
    // EpicSourceNode — which is what made a run's node history read as Epic for an athenahealth or eCW pipeline.
    // guessVendorId covers a node that predates __vendorId; the two name tests below stay as the last resort for
    // ids a name match can't produce ('generic-fhir' never appears hyphenated in a display name).
    const vendorId = node.vendorId ?? this.guessVendorId(connector);
    if (vendorId && VENDOR_SOURCE_TRANSFORM_IDS.has(vendorId)) return vendorId;
    if (/sample/i.test(connector)) return 'sample';
    if (/generic.?fhir/i.test(connector)) return 'generic-fhir';
    return 'epic';
  }

  // Best-effort cosmetic vendor guess for a node saved BEFORE __vendorId existed (nodeToRequest above) —
  // every current display name for a gated vendor happens to be built from/around its real name (e.g.
  // "Athena-Backend-SearchRest-Medplum", "Cerner Provider Launch"), so a substring match is enough to
  // undo the mislabeling without a data migration. Checks non-Epic vendors first so a name that happens
  // to also contain "epic" doesn't shadow a real Cerner/Athenahealth/etc. match.
  private guessVendorId(displayName: string): string | undefined {
    const lower = displayName.toLowerCase();
    return SOURCES.find(s => s.id !== 'epic' && lower.includes(s.id))?.id
      ?? (lower.includes('epic') ? 'epic' : undefined);
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
