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
  // Array-aware metadata stamped by the wizard from the backend FHIR catalog. When present these are
  // authoritative; when absent (offline/degraded) we fall back to the naive path conversion below.
  jsonPath?: string;       // e.g. "$.name[*].given[*]"
  valueType?: string;      // String | Integer | Decimal | Boolean | Date | DateTime | Json
  arrays?: string[];       // array-ancestor fhir paths
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
      sources.push({ nodeId: id, source: this.buildSource(fields), existingId: fields['sourceConnectionId'] || null });
    }

    const destinations: DestinationBuildSpec[] = [];
    const mappings: MappingBuildSpec[] = [];

    for (const destNode of graph.nodes.filter(node => this.isDestinationNode(node))) {
      const destFields = this.fieldsFor(destNode.id, nodesById);
      destinations.push({
        nodeId: destNode.id,
        destination: this.buildDestination(destFields, destNode),
        existingId: destFields['destinationId'] || null,
      });

      const mappingNodeId = this.mappingNodeFeeding(destNode.id, graph);
      const sourceNodeId = this.sourceFeeding(mappingNodeId ?? destNode.id, graph, sourceNodeIds);
      if (!mappingNodeId || !sourceNodeId) continue;

      const mappingFields = this.fieldsFor(mappingNodeId, nodesById);
      const mappingSpec = this.buildMapping(mappingNodeId, sourceNodeId, destNode.id, destFields);
      if (mappingSpec) mappings.push({ ...mappingSpec, existingId: mappingFields['mappingProfileId'] || null });
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
      redirectUris: [fields['Redirect URI'] || 'http://localhost:5000/api/v1/oauth/callback'],
      launchUrl: fields['Launch URL'] || null,
      trustedIssuers: (fields['Trusted issuers'] ?? '').split(/[\s,]+/).filter(Boolean),
      patientSelectionMethod: null,
      // Only meaningful for EHR launch — the wizard only shows/populates this field for that audience.
      launchDisplayMode: appType === 'EhrLaunch' ? (fields['Launch display mode'] || null) : null,
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
      // Provider Standalone gets a curated Search REST subset too (Resource Types/Search Criteria/Max Results/
      // Include Related Resources — no scheduler, since it's a user-initiated one-shot fetch, not automated).
      retrieval: appType === 'Backend' || appType === 'Standalone' ? this.buildRetrieval(fields) : null,
    };
  }

  private applicationTypeFor(fields: Record<string, string>): string {
    // Key off the exact app key (wizard.service.ts save() always writes 'App key') rather than fuzzy-matching the
    // human-readable context string — "Patient (standalone)" contains the substring "standalone", so checking
    // ctx.includes('standalone') before ctx.includes('patient') silently misclassified every patient-audience
    // connection as Standalone (requesting user/ clinician scopes instead of patient/, which is why Epic rendered
    // Hyperspace instead of MyChart for patient-facing sources).
    switch (fields['App key']) {
      case 'provider-ehr-launch': return 'EhrLaunch';
      case 'provider-standalone': return 'Standalone';
      case 'patient-standalone': return 'Patient';
      case 'backend-system': return 'Backend';
    }

    // Fallback for connections saved before 'App key' was captured — patient checked before standalone since
    // "Patient (standalone)" contains "standalone" as a substring.
    const ctx = (fields['App context'] ?? fields['Epic audience'] ?? '').toLowerCase();
    if (ctx.includes('ehr')) return 'EhrLaunch';
    if (ctx.includes('patient')) return 'Patient';
    if (ctx.includes('standalone')) return 'Standalone';
    return 'Backend';
  }

  /** Backend System (full method picker) and Provider Standalone (Search REST subset, one-shot — Run
   *  Mode/scheduler fields are never populated so they simply come through as null/default) — maps the wizard's
   *  Retrieval Configuration fields onto the backend's retrieval DTO. Returns null when no retrieval method was
   *  chosen (e.g. the connection is still being drafted). */
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
    // Standalone hides the retrieval method's own Resource Type control and reuses the shared Resource Type &
    // Scopes picker (Section 5, saved under 'Resources') instead — fall back to that when the retrieval-specific
    // one is empty, which it always is for Standalone.
    const retrievalResourceTypes = splitList(fields['Retrieval resource type']);
    const resourceTypes = retrievalResourceTypes.length ? retrievalResourceTypes : splitList(fields['Resources']);

    // Patient ID list is a free-text area — accept comma- or newline-separated ids.
    const patientIds = (fields['Patient ID / list'] ?? '')
      .split(/[\s,]+/).map(v => v.trim()).filter(Boolean);
    const exportScope = fields['Export scope'] || null;
    // The wizard uses short tokens; $export's _outputFormat expects the registered MIME type. Both ndjson variants
    // map to application/fhir+ndjson (gzip is negotiated via transport encoding, not a distinct _outputFormat value).
    const outputFormat = (fields['FHIR output format'] ?? '').startsWith('ndjson')
      ? 'application/fhir+ndjson'
      : null;

    return {
      retrievalMethod,
      resourceTypes,
      searchCriteria: fields['Search criteria'] || null,
      incrementalSyncEnabled: fields['Incremental cursor'] === 'enabled',
      pageSize: toPositiveNumber(fields['Page size (_count)']),
      sortOrder: fields['Sort (_sort)'] || null,
      includeParameters: includeParameters.length ? includeParameters : null,
      revIncludeParameters: revIncludeParameters.length ? revIncludeParameters : null,
      retryPolicy: fields['Retry policy'] || null,
      timeoutSeconds: toPositiveNumber(fields['Timeout (seconds)']),
      maxRecordsPerRun: toPositiveNumber(fields['Max records per run']),
      // Bulk Data $export settings — only meaningful when retrievalMethod === 'bulk-export'.
      exportScope,
      groupId: exportScope === 'group' ? (fields['Group ID'] || null) : null,
      patientIds: exportScope === 'patient' && patientIds.length ? patientIds : null,
      outputFormat,
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

    const fields: MappingFieldRequest[] = primaryRows.map(row => {
      // Prefer the catalog-derived JSONPath/metadata the wizard stamped on the row; fall back to the
      // naive conversion only when the catalog was unavailable.
      const jsonPath = row.jsonPath ?? this.toJsonPath(row.path, primary);
      const arrays = row.arrays ?? [];
      const isArrayPath = jsonPath.includes('[*]') || arrays.length > 0;
      return {
        targetField: row.column,
        jsonPath,
        valueType: row.valueType ?? this.valueTypeFor(row.path),
        isRequired: false,
        defaultValue: null,
        format: null,
        // A flat destination column takes the first match when the path crosses an array; multi-value
        // fan-out (RepeatParent / SeparateDestination) is a deliberate per-field choice, not the default.
        arrayPolicy: isArrayPath ? 'FirstItem' : 'Scalar',
        arrayAncestors: arrays.length > 0 ? arrays : null,
      };
    });

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

  /**
   * Fallback FHIR element path → JSONPath used only when the wizard could not stamp a catalog-derived
   * path on the row (offline/degraded). Strips the leading "Resource." and prefixes "$."; it does NOT
   * infer array-ness — the backend catalog is the source of truth for that (see DestMappingRow.jsonPath).
   */
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
