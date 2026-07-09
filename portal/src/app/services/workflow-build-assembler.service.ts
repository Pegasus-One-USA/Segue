import { Injectable, inject } from '@angular/core';
import { PipelineStore } from './pipeline.store';
import { WorkflowGraphMapperService } from './workflow-graph-mapper.service';
import {
  CreateDestinationConfigurationRequest,
  CreateSourceConnectionRequest,
  DestinationBuildSpec,
  MappingBuildSpec,
  MappingFieldRequest,
  SourceBuildSpec,
  SourceRetrievalConfigurationRequest,
  WorkflowBuildRequest,
  WorkflowNodeRequest,
  WorkflowTriggerRequest,
} from './workflow-api.service';

interface DestMappingRow {
  resource: string;
  field: string;
  path: string;    // FHIR element path captured by the wizard (e.g. "Patient.name.family")
  target: string;  // destination table / file (e.g. "dbo.Patient")
  column: string;  // destination column
}

/**
 * Translates the current builder canvas + wizard fields into a {@link WorkflowBuildRequest} for the Option B
 * create-on-save endpoint (`POST /workflows/build`). Reuses {@link WorkflowGraphMapperService.toRequest} for the graph
 * (nodes/edges/synthetic-mapping) exactly as plain save does, then derives the create-specs that turn the wizard's
 * human-labelled fields into the backend DTO shapes (assembled connection secret, MappingField rows, source auth).
 *
 * Scope / known approximations (see workflow-builder-optionB-handoff.md):
 *  - One mapping profile per destination (the graph model is one mapping node → one destination): if the destination
 *    wizard selected multiple resources, only the PRIMARY (first) resource is wired; `unmappedResources` reports the rest.
 *  - Wizard field paths are FHIR element paths ("Patient.name.family"); the executor wants JSONPath. We do a best-effort
 *    conversion (strip the resource prefix, prefix "$."). Simple scalars like id map cleanly; deep arrays may not populate.
 *  - Epic source auth is mapped best-effort from the wizard; trusted issuers / private-key secret are not fully captured.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowBuildAssemblerService {
  private readonly store = inject(PipelineStore);
  private readonly mapper = inject(WorkflowGraphMapperService);

  /** Resources selected on a destination but NOT wired into the build (surfaced to the user as a caveat). */
  readonly lastUnmappedResources: string[] = [];

  assemble(name: string, trigger?: WorkflowTriggerRequest | null): WorkflowBuildRequest {
    this.lastUnmappedResources.length = 0;

    const graph = this.mapper.toRequest(name, trigger);
    const nodesById = new Map(graph.nodes.map(node => [node.id, node]));

    const sourceNodeIds = new Set(
      graph.nodes.filter(node => this.isSourceNode(node)).map(node => node.id),
    );

    const sources: SourceBuildSpec[] = [];
    for (const id of sourceNodeIds) {
      const fields = this.fieldsFor(id, nodesById);
      sources.push({ nodeId: id, source: this.buildSource(fields) });
    }

    const destinations: DestinationBuildSpec[] = [];
    const mappings: MappingBuildSpec[] = [];

    for (const destNode of graph.nodes.filter(node => this.isDestinationNode(node))) {
      const destFields = this.fieldsFor(destNode.id, nodesById);
      destinations.push({ nodeId: destNode.id, destination: this.buildDestination(destFields, destNode) });

      const mappingNodeId = this.mappingNodeFeeding(destNode.id, graph);
      const sourceNodeId = this.sourceFeeding(mappingNodeId ?? destNode.id, graph, sourceNodeIds);
      if (!mappingNodeId || !sourceNodeId) continue;

      const mappingSpec = this.buildMapping(mappingNodeId, sourceNodeId, destNode.id, destFields);
      if (mappingSpec) mappings.push(mappingSpec);
    }

    return { ...graph, sources, destinations, mappings };
  }

  // ── source ────────────────────────────────────────────────────────────────
  private buildSource(fields: Record<string, string>): CreateSourceConnectionRequest {
    const connector = fields['Connector'] ?? fields['__name'] ?? '';
    const isSample = /sample/i.test(connector) || /sample/i.test(fields['__name'] ?? '');

    if (isSample) {
      return {
        name: fields['__name'] || 'Sample Source',
        sourceSystemType: 'Sample',
        baseUrl: fields['FHIR base URL'] || 'https://sample.local/fhir',
        authentication: { authenticationType: 'None', scopes: [] },
        applicationType: null,
        interactive: null,
      };
    }

    // Epic (best-effort from the Epic source wizard fields).
    const scopes = (fields['Scopes'] ?? '').split(/[\s,]+/).filter(Boolean);
    const appType = this.applicationTypeFor(fields);
    const interactive = appType === 'Backend' ? null : {
      redirectUris: [fields['Redirect URI'] || 'https://fhirbridge.com/oauth/callback'],
      launchUrl: fields['Launch URL'] || null,
      trustedIssuers: (fields['Trusted issuers'] ?? '').split(/[\s,]+/).filter(Boolean),
      patientSelectionMethod: null,
    };

    return {
      name: fields['__name'] || 'Epic',
      sourceSystemType: 'Epic',
      baseUrl: fields['FHIR base URL'] || '',
      authentication: {
        authenticationType: appType === 'Backend' ? 'SmartBackendServices' : 'None',
        clientId: fields['Client ID'] || fields['Active client ID'] || null,
        tokenEndpoint: fields['Token endpoint'] || null,
        scopes,
        keyId: fields['JWT kid'] || null,
        // Backend Services signs its JWT assertion with a private key referenced by (Key Vault Name, Secret Name) —
        // required by ConfigurationService.ValidateEpicSourceConnection for any non-interactive Epic source.
        privateKeyKeyVaultName: fields['Key vault reference'] || null,
        privateKeySecretName: fields['Secret Name'] || null,
      },
      applicationType: appType,
      interactive,
      retrieval: appType === 'Backend' ? this.buildRetrieval(fields) : null,
    };
  }

  private applicationTypeFor(fields: Record<string, string>): string {
    const ctx = (fields['App context'] ?? fields['Epic audience'] ?? '').toLowerCase();
    if (ctx.includes('ehr')) return 'EhrLaunch';
    if (ctx.includes('standalone')) return 'Standalone';
    if (ctx.includes('patient')) return 'Patient';
    return 'Backend';
  }

  /** Backend System only — maps the wizard's Retrieval Configuration fields onto the backend's retrieval DTO.
   *  Returns null when no retrieval method was chosen (e.g. the connection is still being drafted). */
  private buildRetrieval(fields: Record<string, string>): SourceRetrievalConfigurationRequest | null {
    const retrievalMethod = fields['Retrieval method key'];
    if (!retrievalMethod) return null;

    const splitList = (raw: string | undefined): string[] =>
      (raw ?? '').split(',').map(v => v.trim()).filter(Boolean);
    const toPositiveNumber = (raw: string | undefined): number | null => {
      const n = Number(raw);
      return raw && Number.isFinite(n) && n > 0 ? n : null;
    };

    const includeParameters = splitList(fields['Include (_include)']);
    const revIncludeParameters = splitList(fields['Reverse include (_revinclude)']);

    return {
      retrievalMethod,
      resourceTypes: splitList(fields['Retrieval resource type']),
      searchCriteria: fields['Search criteria'] || null,
      incrementalSyncEnabled: fields['Incremental cursor'] === 'enabled',
      pageSize: toPositiveNumber(fields['Page size (_count)']),
      sortOrder: fields['Sort (_sort)'] || null,
      includeParameters: includeParameters.length ? includeParameters : null,
      revIncludeParameters: revIncludeParameters.length ? revIncludeParameters : null,
      retryPolicy: fields['Retry policy'] || null,
      timeoutSeconds: toPositiveNumber(fields['Timeout (seconds)']),
      maxRecordsPerRun: toPositiveNumber(fields['Max records per run']),
    };
  }

  // ── destination ─────────────────────────────────────────────────────────────
  private buildDestination(
    fields: Record<string, string>,
    node: WorkflowNodeRequest,
  ): CreateDestinationConfigurationRequest {
    const isSql = node.nodeType.includes('SqlServer') || (fields['__transformId'] ?? '') === 'dest-sqlserver';
    const name = fields['dest_name'] || (isSql ? 'SQL Destination' : 'File Destination');
    const secretName = `dest-${this.slug(name)}-${this.shortId()}`;

    if (isSql) {
      return {
        name,
        destinationType: 'SqlServer',
        keyVaultName: 'workflow-secrets',
        secretName,
        target: null,
        inlineSecret: this.buildSqlConnectionString(fields),
      };
    }

    return {
      name,
      destinationType: 'Sftp',
      keyVaultName: 'workflow-secrets',
      secretName,
      target: fields['dest_filePattern'] || null,
      inlineSecret: this.buildSftpUri(fields),
    };
  }

  private buildSqlConnectionString(f: Record<string, string>): string {
    const server = f['dest_server'] ?? '';
    const database = f['dest_database'] ?? '';
    const parts = [`Server=${server}`, `Database=${database}`];
    if ((f['dest_auth'] ?? 'sql-auth') === 'sql-auth') {
      parts.push(`User Id=${f['dest_username'] ?? ''}`, `Password=${f['dest_password'] ?? ''}`);
    } else {
      parts.push('Authentication=Active Directory Default');
    }
    parts.push('TrustServerCertificate=True', 'Encrypt=True');
    return parts.join(';');
  }

  private buildSftpUri(f: Record<string, string>): string {
    const user = encodeURIComponent(f['dest_sftpUsername'] ?? '');
    const pass = encodeURIComponent(f['dest_sftpPassword'] ?? '');
    const host = f['dest_sftpHost'] ?? '';
    const port = f['dest_sftpPort'] ?? '22';
    const folder = (f['dest_sftpRemoteFolder'] ?? f['dest_folder'] ?? '').replace(/^\/+/, '');
    return `sftp://${user}:${pass}@${host}:${port}/${folder}`;
  }

  // ── mapping ───────────────────────────────────────────────────────────────
  private buildMapping(
    mappingNodeId: string,
    sourceNodeId: string,
    destinationNodeId: string,
    destFields: Record<string, string>,
  ): MappingBuildSpec | null {
    const rows = this.parseMappingRows(destFields['dest_mappings']);
    const resources = [...new Set(rows.map(row => row.resource))];
    if (resources.length === 0) return null;

    const primary = resources[0];
    if (resources.length > 1) this.lastUnmappedResources.push(...resources.slice(1));

    const primaryRows = rows.filter(row => row.resource === primary);
    const destinationObject = primaryRows[0]?.target || this.targetForResource(destFields, primary) || primary;

    const fields: MappingFieldRequest[] = primaryRows.map(row => ({
      targetField: row.column,
      jsonPath: this.toJsonPath(row.path, primary),
      valueType: this.valueTypeFor(row.path),
      isRequired: false,
      defaultValue: null,
      format: null,
    }));

    return {
      nodeId: mappingNodeId,
      sourceNodeId,
      destinationNodeId,
      name: `${primary} mapping`,
      resourceType: primary,
      destinationObject,
      fields,
    };
  }

  private parseMappingRows(json: string | undefined): DestMappingRow[] {
    if (!json) return [];
    try {
      const parsed = JSON.parse(json) as DestMappingRow[];
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  private targetForResource(fields: Record<string, string>, resource: string): string | null {
    try {
      const targets = JSON.parse(fields['dest_targets'] ?? '{}') as Record<string, string>;
      return targets[resource] ?? null;
    } catch {
      return null;
    }
  }

  /** Best-effort FHIR element path → JSONPath: strip the leading "Resource." and prefix "$.". */
  private toJsonPath(path: string, resourceType: string): string {
    let p = path.trim();
    if (p.startsWith(`${resourceType}.`)) p = p.slice(resourceType.length + 1);
    if (p.startsWith('$')) return p;
    return `$.${p}`;
  }

  private valueTypeFor(path: string): string {
    const p = path.toLowerCase();
    if (p.includes('birthdate') || p.includes('onset') || /date($|[^t])/.test(p)) return 'Date';
    if (p.includes('datetime') || p.includes('period') || p.includes('effective')) return 'DateTime';
    return 'String';
  }

  // ── graph helpers ───────────────────────────────────────────────────────────
  private isSourceNode(node: WorkflowNodeRequest): boolean {
    return node.category === 0 || node.category === 'Source';
  }

  private isDestinationNode(node: WorkflowNodeRequest): boolean {
    return node.nodeType.endsWith('DestinationNode');
  }

  private mappingNodeFeeding(destNodeId: string, graph: WorkflowBuildRequest): string | null {
    const edge = graph.edges.find(e => e.toNodeId === destNodeId);
    return edge?.fromNodeId ?? null;
  }

  private sourceFeeding(
    startNodeId: string,
    graph: WorkflowBuildRequest,
    sourceNodeIds: Set<string>,
  ): string | null {
    let current: string | null = startNodeId;
    const seen = new Set<string>();
    while (current && !seen.has(current)) {
      if (sourceNodeIds.has(current)) return current;
      seen.add(current);
      current = graph.edges.find(e => e.toNodeId === current)?.fromNodeId ?? null;
    }
    // Fallback: the first source in the graph (linear single-source pipelines).
    return [...sourceNodeIds][0] ?? null;
  }

  private fieldsFor(nodeId: string, nodesById: Map<string, WorkflowNodeRequest>): Record<string, string> {
    // Prefer the live store node fields; fall back to the serialized config on the graph node.
    const storeNode = this.store.byId(nodeId);
    if (storeNode) return storeNode.fields;
    const node = nodesById.get(nodeId);
    if (!node?.configurationJson) return {};
    try {
      const parsed = JSON.parse(node.configurationJson) as Record<string, unknown>;
      return Object.fromEntries(
        Object.entries(parsed).map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v)]),
      );
    } catch {
      return {};
    }
  }

  private slug(value: string): string {
    return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 24) || 'dest';
  }

  private shortId(): string {
    return Math.random().toString(36).slice(2, 8);
  }
}
