import { Injectable, inject } from '@angular/core';
import { PipelineStore } from './pipeline.store';
import { WorkflowGraphMapperService } from './workflow-graph-mapper.service';
import { OAUTH_DEFAULT_URLS } from '../core/api-endpoints';
import {
  CreateDestinationConfigurationRequest,
  CreateSourceConnectionRequest,
  DestinationBuildSpec,
  MappingBuildSpec,
  MappingFieldRequest,
  ParentReferenceSpec,
  SourceBuildSpec,
  SourceRetrievalConfigurationRequest,
  WorkflowBuildRequest,
  WorkflowNodeRequest,
  WorkflowTriggerRequest,
} from './workflow-api.service';

interface DestMappingRow {
  resource: string;
  field: string;
  path: string; // FHIR element path captured by the wizard (e.g. "Patient.name.family")
  target: string; // destination table / file (e.g. "dbo.Patient")
  column: string; // destination column
  // Array-aware metadata stamped by the wizard from the backend FHIR catalog. When present these are
  // authoritative; when absent (offline/degraded) we fall back to the naive path conversion below.
  jsonPath?: string; // e.g. "$.name[*].given[*]"
  valueType?: string; // String | Integer | Decimal | Boolean | Date | DateTime | Json
  arrays?: string[]; // array-ancestor fhir paths
  isUpsertKey?: boolean; // wizard-forced true on the resource's mandatory id row, false elsewhere
  isRequiredParentRef?: boolean; // wizard-forced true on a locked "child of" reference-field row
  parentResourceType?: string;   // which parent (of possibly several) this locked row satisfies
  // Advanced MappingFieldDto members the wizard has no UI to author (docs/backend/14-mapping-profile-master-screen-plan.md
  // §3.2) but must still round-trip losslessly when present — e.g. a row loaded from a profile the Mapping
  // Profiles master screen authored. undefined for every wizard-authored row, in which case the computed
  // defaults below (isRequired/arrayPolicy/etc.) apply exactly as before.
  isRequired?: boolean;
  defaultValue?: string;
  format?: string;
  normalizationType?: string;
  terminologySystemJsonPath?: string;
  terminologyCodeJsonPath?: string;
  arrayPolicy?: string;
  cardinality?: string;
  correlationCodeJsonPath?: string;
  correlationCodeValue?: string;
  isEnabled?: boolean;
  // True when this row's field-mapping metadata (JsonPath/arrayPolicy/etc.) was derived by naive path
  // conversion rather than the backend FHIR catalog's own authoritative shape — see field-mapping-model.ts.
  approximated?: boolean;
  // Set by field-mapping-model.ts's serializeRowsFlat only when `target` is a genuine child table of this
  // resource's own primary table (e.g. dbo.PatientName, child of dbo.Patient) — lets buildMappingForResource
  // route this one field to its own table via MappingFieldRequest.destinationObject rather than the
  // resource's single baseDestinationObject, exactly mirroring MappingImportService.BuildFieldAsync on the
  // backend for the mapping-profiles/import path.
  parentTable?: string;
  parentKeyColumn?: string;
  foreignKeyColumn?: string;
  // Set by field-mapping-list.component.ts's "which resource does this reference?" picker (round-tripped
  // through field-mapping-model.ts's serializeRowsFlat) — resolved in buildMappingForResource into the
  // referenced resource's own table/id column, since the raw FHIR reference string ("Patient/xyz") this field
  // is sourced from can never be written as-is into what's normally a NOT NULL FK column.
  referencesResource?: string;
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

  assemble(
    name: string,
    trigger?: WorkflowTriggerRequest | null,
  ): WorkflowBuildRequest {
    this.lastUnmappedResources.length = 0;

    const graph = this.mapper.toRequest(name, trigger);
    const nodesById = new Map(graph.nodes.map((node) => [node.id, node]));

    const sourceNodeIds = new Set(
      graph.nodes
        .filter((node) => this.isSourceNode(node))
        .map((node) => node.id),
    );

    const sources: SourceBuildSpec[] = [];
    for (const id of sourceNodeIds) {
      const fields = this.fieldsFor(id, nodesById);
      // Existing-source pick left untouched (see EpicAudienceFormComponent.save()'s resolvedSourceConnectionId) —
      // skip entirely, no create/update. Mirrors destinationResolved below: a connection another workflow also
      // points at can't be mutated by this save, and the backend resolves sourceConnectionId straight off this
      // node's own config for the Mappings step regardless.
      if (fields['sourceConnectionResolved'] === 'true') continue;
      sources.push({
        nodeId: id,
        source: this.buildSource(fields),
        existingId: fields['sourceConnectionId'] || null,
      });
    }

    const destinations: DestinationBuildSpec[] = [];
    const mappings: MappingBuildSpec[] = [];

    for (const destNode of graph.nodes.filter((node) =>
      this.isDestinationNode(node),
    )) {
      const destFields = this.fieldsFor(destNode.id, nodesById);

      // destinationResolved: the wizard selected an existing connection and left it untouched — its destinationId/
      // secretKeyVaultName/secretName/target are already final on the node's own config (see
      // DestinationWizardComponent._save()), so this destination is skipped here entirely. No create, no update —
      // a connection another workflow also points at can't be mutated by this save.
      if (destFields['destinationResolved'] !== 'true') {
        destinations.push({
          nodeId: destNode.id,
          destination: this.buildDestination(destFields, destNode),
          existingId: destFields['destinationId'] || null,
        });
      }

      const mappingNodeId = this.mappingNodeFeeding(destNode.id, graph);
      const sourceNodeId = this.sourceFeeding(
        mappingNodeId ?? destNode.id,
        graph,
        sourceNodeIds,
      );
      if (!mappingNodeId || !sourceNodeId) continue;

      const mappingFields = this.fieldsFor(mappingNodeId, nodesById);
      mappings.push(
        ...this.buildMappings(
          mappingNodeId,
          sourceNodeId,
          destNode.id,
          destFields,
          mappingFields,
        ),
      );
    }

    return { ...graph, sources, destinations, mappings };
  }

  // ── source ────────────────────────────────────────────────────────────────
  private buildSource(
    fields: Record<string, string>,
  ): CreateSourceConnectionRequest {
    const connector = fields['Connector'] ?? fields['__name'] ?? '';
    const isSample =
      /sample/i.test(connector) || /sample/i.test(fields['__name'] ?? '');

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

    // Generic FHIR (GenericFhirSourceFormComponent) — a bare, unauthenticated conformant FHIR R4 server. No
    // OAuth/interactive concepts apply, so applicationType/interactive stay null (same as Sample). Retrieval
    // reuses the exact same buildRetrieval() Epic's Backend-System retrieval section feeds — search-rest's
    // _since/_lastUpdated incremental cursor, _count, _sort, _include/_revinclude, and bulk $export's
    // scope/group/patient/output-format are all vendor-agnostic on the backend, not Epic-specific.
    if (/generic.?fhir/i.test(connector)) {
      return {
        name: fields['__name'] || 'Generic FHIR Source',
        sourceSystemType: 'GenericFhir',
        baseUrl: fields['FHIR base URL'] || '',
        authentication: { authenticationType: 'None', scopes: [] },
        applicationType: null,
        interactive: null,
        retrieval: this.buildRetrieval(fields),
      };
    }

    // Epic (best-effort from the Epic source wizard fields).
    const scopes = (fields['Scopes'] ?? '').split(/[\s,]+/).filter(Boolean);
    const appType = this.applicationTypeFor(fields);
    const interactive =
      appType === 'Backend'
        ? null
        : {
            redirectUris: [
              fields['Redirect URI'] ||
                OAUTH_DEFAULT_URLS.redirectUri,
            ],
            launchUrl: fields['Launch URL'] || null,
            trustedIssuers: (fields['Trusted issuers'] ?? '')
              .split(/[\s,]+/)
              .filter(Boolean),
            patientSelectionMethod: null,
            // Only meaningful for EHR launch — the wizard only shows/populates this field for that audience.
            launchDisplayMode:
              appType === 'EhrLaunch'
                ? fields['Launch display mode'] || null
                : null,
          };

    return {
      name: fields['__name'] || 'Epic',
      sourceSystemType: 'Epic',
      baseUrl: fields['FHIR base URL'] || '',
      authentication: {
        authenticationType:
          appType === 'Backend' ? 'SmartBackendServices' : 'None',
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
      retrieval:
        appType === 'Backend' || appType === 'Standalone'
          ? this.buildRetrieval(fields)
          : null,
    };
  }

  private applicationTypeFor(fields: Record<string, string>): string {
    // Key off the exact app key (wizard.service.ts save() always writes 'App key') rather than fuzzy-matching the
    // human-readable context string — "Patient (standalone)" contains the substring "standalone", so checking
    // ctx.includes('standalone') before ctx.includes('patient') silently misclassified every patient-audience
    // connection as Standalone (requesting user/ clinician scopes instead of patient/, which is why Epic rendered
    // Hyperspace instead of MyChart for patient-facing sources).
    switch (fields['App key']) {
      case 'provider-ehr-launch':
        return 'EhrLaunch';
      case 'provider-standalone':
        return 'Standalone';
      case 'patient-standalone':
        return 'Patient';
      case 'backend-system':
        return 'Backend';
    }

    // Fallback for connections saved before 'App key' was captured — patient checked before standalone since
    // "Patient (standalone)" contains "standalone" as a substring.
    const ctx = (
      fields['App context'] ??
      fields['Epic audience'] ??
      ''
    ).toLowerCase();
    if (ctx.includes('ehr')) return 'EhrLaunch';
    if (ctx.includes('patient')) return 'Patient';
    if (ctx.includes('standalone')) return 'Standalone';
    return 'Backend';
  }

  /** Backend System (full method picker) and Provider Standalone (Search REST subset, one-shot — Run
   *  Mode/scheduler fields are never populated so they simply come through as null/default) — maps the wizard's
   *  Retrieval Configuration fields onto the backend's retrieval DTO. Returns null when no retrieval method was
   *  chosen (e.g. the connection is still being drafted). */
  private buildRetrieval(
    fields: Record<string, string>,
  ): SourceRetrievalConfigurationRequest | null {
    const retrievalMethod = fields['Retrieval method key'];
    if (!retrievalMethod) return null;

    const splitList = (raw: string | undefined): string[] =>
      (raw ?? '')
        .split(',')
        .map((v) => v.trim())
        .filter(Boolean);
    const toPositiveNumber = (raw: string | undefined): number | null => {
      const n = Number(raw);
      return raw && Number.isFinite(n) && n > 0 ? n : null;
    };

    const includeParameters = splitList(fields['Include (_include)']);
    const revIncludeParameters = splitList(
      fields['Reverse include (_revinclude)'],
    );
    // Standalone hides the retrieval method's own Resource Type control and reuses the shared Resource Type &
    // Scopes picker (Section 5, saved under 'Resources') instead — fall back to that when the retrieval-specific
    // one is empty, which it always is for Standalone.
    const retrievalResourceTypes = splitList(fields['Retrieval resource type']);
    const resourceTypes = retrievalResourceTypes.length
      ? retrievalResourceTypes
      : splitList(fields['Resources']);

    // Patient ID list is a free-text area — accept comma- or newline-separated ids.
    const patientIds = (fields['Patient ID / list'] ?? '')
      .split(/[\s,]+/)
      .map((v) => v.trim())
      .filter(Boolean);
    const exportScope = fields['Export scope'] || null;
    // The wizard uses short tokens; $export's _outputFormat expects the registered MIME type. Both ndjson variants
    // map to application/fhir+ndjson (gzip is negotiated via transport encoding, not a distinct _outputFormat value).
    const outputFormat = (fields['FHIR output format'] ?? '').startsWith(
      'ndjson',
    )
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
      revIncludeParameters: revIncludeParameters.length
        ? revIncludeParameters
        : null,
      retryPolicy: fields['Retry policy'] || null,
      timeoutSeconds: toPositiveNumber(fields['Timeout (seconds)']),
      maxRecordsPerRun: toPositiveNumber(fields['Max records per run']),
      // Bulk Data $export settings — only meaningful when retrievalMethod === 'bulk-export'.
      exportScope,
      groupId: exportScope === 'group' ? fields['Group ID'] || null : null,
      patientIds:
        exportScope === 'patient' && patientIds.length ? patientIds : null,
      outputFormat,
    };
  }

  // ── destination ─────────────────────────────────────────────────────────────
  private buildDestination(
    fields: Record<string, string>,
    node: WorkflowNodeRequest,
  ): CreateDestinationConfigurationRequest {
    const isMySql =
      node.nodeType.includes('MySql') ||
      (fields['__transformId'] ?? '') === 'dest-mysql';
    const isPostgres =
      node.nodeType.includes('PostgreSql') ||
      (fields['__transformId'] ?? '') === 'dest-postgres';
    const isSql =
      isMySql ||
      isPostgres ||
      node.nodeType.includes('SqlServer') ||
      (fields['__transformId'] ?? '') === 'dest-sqlserver';
    const isMongo =
      node.nodeType.includes('Mongo') ||
      (fields['__transformId'] ?? '') === 'dest-mongo';
    const isBlob =
      node.nodeType.includes('Blob') ||
      (fields['__transformId'] ?? '') === 'dest-blob';
    const name =
      fields['dest_name'] || (isMySql ? 'MySQL Destination' : isPostgres ? 'PostgreSQL Destination' : isSql ? 'SQL Destination' : isMongo ? 'MongoDB Destination' : isBlob ? 'Azure Blob Destination' : 'File Destination');
    // Reuse the secret reference from a prior build (injected back onto this node's config as secretKeyVaultName/
    // secretName — see WorkflowEndpoints.MapWorkflowEndpoints's Destinations step) so re-saving an existing
    // destination overwrites its ProvisionedSecrets row via WriteSecretAsync's (KeyVaultName, SecretName) upsert
    // instead of minting a brand-new name every save, which only ever inserts a new row and orphans the old one.
    const keyVaultName = fields['secretKeyVaultName'] || 'workflow-secrets';
    const secretName =
      fields['secretName'] || `dest-${this.slug(name)}-${this.shortId()}`;

    // dest_password/dest_sftpPassword are redacted from persisted config (see WorkflowGraphMapperService's
    // SECRET_FIELD_KEYS) and never round-trip back into the wizard on reload — a blank password field on a
    // destination that's ALREADY been provisioned (it already carries a secretName from a prior build) means
    // "the wizard never had a password to show", not "the user wants to blank out a working credential". Rebuilding
    // the connection string/URI anyway would send a non-blank string with an empty password embedded in it, which
    // the backend's own "don't touch the secret if none was sent" guard (ConfigurationService's
    // UpdateDestinationConfigurationAsync, checking IsNullOrWhiteSpace on the WHOLE string) can't catch — silently
    // overwriting a working credential with a broken one on every no-op re-save. Only rebuild when a password was
    // actually entered, or this is a brand-new destination with nothing to preserve yet.
    const hasExistingSecret = !!fields['secretName'];

    if (isSql) {
      return {
        name,
        destinationType: isMySql ? 'MySql' : isPostgres ? 'PostgreSql' : 'SqlServer',
        keyVaultName,
        secretName,
        target: null,
        inlineSecret:
          hasExistingSecret && !fields['dest_password']
            ? null
            : this.buildSqlConnectionString(fields, isMySql, isPostgres),
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'sql'),
      };
    }

    if (isMongo) {
      return {
        name,
        destinationType: 'Mongo',
        keyVaultName,
        secretName,
        target: fields['dest_collection'] || null,
        // The whole connection string is treated as secret (see destination-wizard.component.ts's mongoForm
        // comment) — there's no split server/database/credentials form to assemble from, so this is a direct
        // pass-through of whatever the wizard collected, same "don't touch an already-provisioned secret unless
        // the user actually typed a new one" guard the SQL/SFTP branches use.
        inlineSecret:
          hasExistingSecret && !fields['dest_connectionString']
            ? null
            : fields['dest_connectionString'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'mongo'),
      };
    }

    if (isBlob) {
      return {
        name,
        destinationType: 'BlobStorage',
        keyVaultName,
        secretName,
        target: fields['dest_blobContainer'] || null,
        // Managed Identity never resolves a Key Vault secret (see BlobDestinationSettings.RequiresSecret
        // server-side) — always sent as '' for that mode, same "don't touch an already-provisioned secret
        // unless the user actually typed a new one" guard the SQL/SFTP branches use otherwise.
        inlineSecret:
          fields['dest_blobAuthMode'] === 'managedIdentity'
            ? ''
            : hasExistingSecret && !fields['dest_blobSecret']
              ? null
              : fields['dest_blobSecret'] || '',
        connectionMetadataJson: this.buildConnectionMetadata(fields, 'blob'),
      };
    }

    const isSftp = fields['dest_deliveryMode'] === 'sftp';
    return {
      name,
      // Always 'Csv': the delivery mode (download/email/sftp/download-link) is a ConnectionMetadataJson field
      // (dest_deliveryMode), not the DestinationType — previously this always hardcoded 'Sftp' regardless of the
      // chosen delivery mode, producing a broken empty-host sftp:// secret for every other mode.
      destinationType: 'Csv',
      keyVaultName,
      secretName,
      target: fields['dest_filePattern'] || null,
      inlineSecret: !isSftp
        ? ''
        : hasExistingSecret && !fields['dest_sftpPassword']
          ? null
          : this.buildSftpUri(fields),
      connectionMetadataJson: this.buildConnectionMetadata(fields, 'csv'),
    };
  }

  /** Non-secret dest_* fields as a flat JSON object — everything above EXCEPT dest_password/dest_sftpPassword/
   *  dest_connectionString, which only ever live in the encrypted secret (buildSqlConnectionString/buildSftpUri/
   *  the Mongo pass-through), never here. Mirrors destination-connection-secret.util.ts's buildConnectionMetadata
   *  — duplicated rather than imported for the same reason buildSqlConnectionString/buildSftpUri are (see that
   *  file's own header comment). */
  private buildConnectionMetadata(
    f: Record<string, string>,
    kind: 'sql' | 'mongo' | 'blob' | 'csv',
  ): string {
    const keys =
      kind === 'sql'
        ? [
            'dest_name',
            'dest_server',
            'dest_database',
            'dest_auth',
            'dest_username',
            'dest_schema',
            'dest_writeMode',
            'dest_requireSsl',
          ]
        : kind === 'mongo'
          ? ['dest_name', 'dest_collection', 'dest_writeMode']
          : kind === 'blob'
            ? [
                'dest_name',
                'dest_blobAuthMode',
                'dest_blobContainer',
                'dest_blobAccountUrl',
                'dest_blobAccountName',
                'dest_blobEndpointSuffix',
                'dest_blobTenantId',
                'dest_blobClientId',
                'dest_blobManagedIdentityClientId',
                'dest_blobPathPrefix',
                'dest_blobCreateContainerIfNotExists',
              ]
            : [
              'dest_name',
              'dest_deliveryMode',
              'dest_filePattern',
              'dest_delimiter',
              'dest_encoding',
              'dest_sftpHost',
              'dest_sftpPort',
              'dest_sftpUsername',
              'dest_sftpAuthType',
              'dest_sftpRemoteFolder',
              'dest_emailTo',
              'dest_emailCc',
              'dest_emailSubjectTemplate',
              'dest_emailBodyTemplate',
              'dest_downloadLinkExpiryMinutes',
            ];
    const metadata: Record<string, string> = {};
    for (const key of keys) {
      if (f[key] !== undefined) metadata[key] = f[key];
    }
    return JSON.stringify(metadata);
  }

  private buildSqlConnectionString(f: Record<string, string>, isMySql = false, isPostgres = false): string {
    const server = f['dest_server'] ?? '';
    const database = f['dest_database'] ?? '';
    const requireSsl = f['dest_requireSsl'] === 'true';
    if (isPostgres) {
      // Npgsql uses Host (not Server) and Username (not User Id); "Require" mode encrypts without validating
      // the server certificate, so no separate "trust cert" flag is needed. Off by default — a local/docker
      // Postgres with SSL disabled would otherwise refuse to connect — checked for providers that enforce it
      // (e.g. AWS RDS's rds.force_ssl).
      return [
        `Host=${server}`,
        `Database=${database}`,
        `Username=${f['dest_username'] ?? ''}`,
        `Password=${f['dest_password'] ?? ''}`,
        `SSL Mode=${requireSsl ? 'Require' : 'Prefer'}`,
      ].join(';');
    }
    const parts = [`Server=${server}`, `Database=${database}`];
    if (isMySql) {
      // MySqlConnector's connection string builder rejects SQL-Server-only keywords
      // (TrustServerCertificate/Encrypt/Authentication=Active Directory Default), so MySQL always
      // authenticates with the username/password entered in the (shared) SQL-family wizard form.
      parts.push(
        `User Id=${f['dest_username'] ?? ''}`,
        `Password=${f['dest_password'] ?? ''}`,
        `SslMode=${requireSsl ? 'Required' : 'Preferred'}`,
      );
      return parts.join(';');
    }
    if ((f['dest_auth'] ?? 'sql-auth') === 'sql-auth') {
      parts.push(
        `User Id=${f['dest_username'] ?? ''}`,
        `Password=${f['dest_password'] ?? ''}`,
      );
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
    const folder = (
      f['dest_sftpRemoteFolder'] ??
      f['dest_folder'] ??
      ''
    ).replace(/^\/+/, '');
    return `sftp://${user}:${pass}@${host}:${port}/${folder}`;
  }

  // ── mapping ───────────────────────────────────────────────────────────────
  // One MappingBuildSpec per resource the destination actually selected — a destination picking Patient +
  // Observation + Condition produces three specs, all sharing the same canvas "Field Mapping" node id (that one
  // node's wizard-authored dest_mappings already carries every resource's field rows, tagged by resource). The
  // backend creates one MappingProfile per spec and accumulates their ids onto that shared node (see
  // WorkflowEndpoints.cs's Mappings step) rather than each overwriting the last — previously only the first
  // (primary) resource ever got a real profile, and every other selected resource silently inherited the
  // primary's field mappings at run time instead of its own (see MappingNodeExecutor).
  private buildMappings(
    mappingNodeId: string,
    sourceNodeId: string,
    destinationNodeId: string,
    destFields: Record<string, string>,
    mappingFields: Record<string, string>,
  ): MappingBuildSpec[] {
    const rows = this.parseMappingRows(destFields['dest_mappings']);
    const resources = [...new Set(rows.map((row) => row.resource))];
    if (resources.length === 0) return [];

    const existingIdsByResource = this.parseExistingMappingProfileIds(mappingFields);

    return resources.map((resource) => {
      const spec = this.buildMappingForResource(
        mappingNodeId,
        sourceNodeId,
        destinationNodeId,
        destFields,
        rows,
        resource,
      );
      return {
        ...spec,
        existingId:
          existingIdsByResource[resource] ??
          // Legacy single-resource node from before mappingProfileIds existed: its one profile id was only ever
          // stored under the flat mappingProfileId field, with no resource tagging at all.
          (resources.length === 1 ? mappingFields['mappingProfileId'] || null : null),
      };
    });
  }

  private parseExistingMappingProfileIds(
    mappingFields: Record<string, string>,
  ): Record<string, string> {
    const raw = mappingFields['mappingProfileIds'];
    if (!raw) return {};
    try {
      const parsed = JSON.parse(raw) as unknown;
      return parsed && typeof parsed === 'object'
        ? (parsed as Record<string, string>)
        : {};
    } catch {
      return {};
    }
  }

  private buildMappingForResource(
    mappingNodeId: string,
    sourceNodeId: string,
    destinationNodeId: string,
    destFields: Record<string, string>,
    rows: DestMappingRow[],
    resource: string,
  ): MappingBuildSpec {
    const resourceRows = rows.filter((row) => row.resource === resource);
    // dest_targets (the wizard's own per-resource "which table is primary" record) is authoritative and
    // must be checked BEFORE resourceRows[0]?.target — now that a row's target correctly reflects its own
    // table (see field-mapping-model.ts's serializeRowsFlat), resourceRows[0] could just as easily be a
    // child-table row as the primary one, and array order here isn't meaningful.
    const baseDestinationObject =
      this.targetForResource(destFields, resource) ||
      resourceRows[0]?.target ||
      resource;
    // The destination wizard's "Write mode" (dw-writeMode) is only ever stashed on dest_writeMode for display —
    // nothing previously translated it into the ;mode=upsert suffix MappedSqlServerDestinationWriter actually
    // reads, so picking "Upsert by source id" in the UI silently still did a blind INSERT. The writer resolves the
    // key column from whichever mapped field is flagged isUpsertKey (the wizard forces this on the resource's
    // mandatory id row — see destination-wizard.component.ts's ID-row reconciliation) rather than a query-string
    // option, so "by source id" only means something once that field is present. jsonPath === '$.id' is kept as a
    // fallback match for mapping rows saved before isUpsertKey existed on a node.
    const idRow =
      resourceRows.find((row) => row.isUpsertKey) ??
      resourceRows.find((row) => (row.jsonPath ?? this.toJsonPath(row.path, resource)) === '$.id');

    let destinationObject = baseDestinationObject;
    const writeMode = destFields['dest_writeMode'];
    if (writeMode === 'upsert' || writeMode === 'update') {
      if (!idRow) {
        const modeLabel = writeMode === 'upsert' ? 'Upsert by source id' : 'Update only';
        throw new Error(
          `"${resource}" destination is set to ${modeLabel}, but no destination column is mapped from ` +
            `${resource}.id. Map the resource's id field to a column, or switch Write mode to Insert only.`,
        );
      }
      destinationObject = `${baseDestinationObject};mode=${writeMode}`;
    }

    const fields: MappingFieldRequest[] = resourceRows.map((row) => {
      // Prefer the catalog-derived JSONPath/metadata the wizard stamped on the row; fall back to the
      // naive conversion only when the catalog was unavailable.
      const jsonPath = row.jsonPath ?? this.toJsonPath(row.path, resource);
      const arrays = row.arrays ?? [];
      const isArrayPath = jsonPath.includes('[*]') || arrays.length > 0;
      // A row whose own table differs from this resource's baseDestinationObject is a genuine child-table
      // field (e.g. Patient.name.use -> dbo.PatientName) — route it there explicitly via a per-field
      // destinationObject override, same as MappingImportService.BuildFieldAsync does for mapping-profiles
      // /import. Every ordinary same-table field omits this (undefined), keeping the wire payload unchanged
      // from before this existed.
      const isChildTableField = !!row.target && row.target !== baseDestinationObject;
      return {
        targetField: row.column,
        jsonPath,
        ...(isChildTableField ? {
          destinationObject: row.target,
          parentTable: row.parentTable ?? null,
          parentKeyColumn: row.parentKeyColumn ?? null,
          foreignKeyColumn: row.foreignKeyColumn ?? null,
        } : {}),
        // Resolves the field-mapping-list "which resource does this reference?" picker into the referenced
        // resource's own table/id column — without this a FHIR reference field (e.g. Observation.subject.
        // reference) keeps writing the raw "Patient/xyz" string, or NULL, into what's normally a NOT NULL FK
        // column, on every save, regardless of what the user picked in that dropdown.
        ...(row.referencesResource ? this.resolveReferenceLookup(rows, row.referencesResource) : {}),
        // The field-mapping canvas always writes SeparateDestination child-table rows as StoreJson-shaped
        // Json regardless of the naive path-derived type, matching the backend engine's own StoreJson handling.
        valueType: row.arrayPolicy === 'StoreJson' ? 'Json' : (row.valueType ?? this.valueTypeFor(row.path)),
        // The id/Upsert-key row is structurally mandatory (every resource always has an id) — flagging it
        // required here matches reality and satisfies the backend's NOT NULL-vs-IsRequired check
        // (CreateMappingProfileRequestValidator) for destinations whose key column is NOT NULL, which is
        // virtually always the case for a primary key. Every other row defaults to optional; a locked
        // "child of" reference row is upgraded to required separately below. A row carrying its own
        // isRequired (loaded from a profile authored outside the wizard) overrides this computed default.
        isRequired: row.isRequired ?? row === idRow,
        defaultValue: row.defaultValue ?? null,
        format: row.format ?? null,
        normalizationType: row.normalizationType,
        terminologySystemJsonPath: row.terminologySystemJsonPath,
        terminologyCodeJsonPath: row.terminologyCodeJsonPath,
        cardinality: row.cardinality,
        // A flat destination column takes the first match when the path crosses an array; multi-value
        // fan-out (RepeatParent / SeparateDestination) is a deliberate per-field choice, not the default.
        arrayPolicy: row.arrayPolicy ?? (isArrayPath ? 'FirstItem' : 'Scalar'),
        arrayAncestors: arrays.length > 0 ? arrays : null,
        isUpsertKey: row.isUpsertKey ?? row === idRow,
        correlationCodeJsonPath: row.correlationCodeJsonPath ?? null,
        correlationCodeValue: row.correlationCodeValue ?? null,
        isEnabled: row.isEnabled,
      };
    });
    // A locked "child of" row is mandatory the same way the id row is — mark it required so the built
    // request reflects that, even though server-side enforcement (ValidateMappingParentReferences) checks
    // presence/JsonPath match rather than this flag.
    for (const row of resourceRows.filter((r) => r.isRequiredParentRef)) {
      const field = fields.find((f) => f.jsonPath === (row.jsonPath ?? this.toJsonPath(row.path, resource)));
      if (field) field.isRequired = true;
    }

    const parentReferences = this.parentReferencesFor(destFields, resource);

    return {
      nodeId: mappingNodeId,
      sourceNodeId,
      destinationNodeId,
      name: `${resource} mapping`,
      resourceType: resource,
      destinationObject,
      fields,
      parentReferences: parentReferences.length ? parentReferences : undefined,
    };
  }

  // Reads the wizard's per-resource "child of" chip selections (dest_parentSelections: Record<child,
  // parent[]>) and turns them into the ParentReferenceSpec[] the backend validates against sibling specs
  // sharing the same destination node.
  private parentReferencesFor(destFields: Record<string, string>, resource: string): ParentReferenceSpec[] {
    try {
      const parsed = JSON.parse(destFields['dest_parentSelections'] ?? '{}') as Record<string, string[]>;
      const parents = parsed[resource] ?? [];
      return parents.map((parentResourceType) => ({ parentResourceType }));
    } catch {
      return [];
    }
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

  private targetForResource(
    fields: Record<string, string>,
    resource: string,
  ): string | null {
    try {
      const targets = JSON.parse(fields['dest_targets'] ?? '{}') as Record<
        string,
        string
      >;
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
    // Checked against the original (not lowercased) path: FHIR's choice-type fields spell out their type as
    // a capitalized suffix (deceasedBoolean, multipleBirthBoolean, valueBoolean, ...), and "active" is FHIR's
    // other common bare boolean field (Patient.active, Practitioner.active, Location.active, ...). Without this,
    // both fell through to the 'String' default below despite the backend catalog itself typing them Boolean.
    if (/Boolean$/.test(path) || /(^|\.)active$/i.test(path)) return 'Boolean';
    const p = path.toLowerCase();
    if (
      p.includes('birthdate') ||
      p.includes('onset') ||
      /date($|[^t])/.test(p)
    )
      return 'Date';
    if (
      p.includes('datetime') ||
      p.includes('period') ||
      p.includes('effective')
    )
      return 'DateTime';
    return 'String';
  }

  /**
   * Resolves a "which resource does this reference?" picker value (row.referencesResource) into the referenced
   * resource's own destination table + the column its own "$.id" field targets. Mirrors
   * field-mapping-summary.model.ts's computeResourceKeyInfo — that copy only feeds dest_mapping_summary_v1's
   * UI-redisplay round-trip; this is the one that actually reaches MappingFieldRequest.referenceLookupTable/
   * referenceLookupKeyColumn, which JsonMappingEngine/MappedSqlServerDestinationWriter read at pipeline-run
   * time to resolve a raw FHIR reference string into the referenced row's real key at write time. Returns {}
   * (never throws) when the referenced resource has no id row yet — an incomplete save shouldn't crash, it
   * should just leave the reference unresolved, same as if the picker had never been touched.
   */
  private resolveReferenceLookup(
    rows: DestMappingRow[],
    referencedResource: string,
  ): { referenceLookupTable?: string; referenceLookupKeyColumn?: string } {
    const idRow = rows.find(
      (r) => r.resource === referencedResource
        && (r.jsonPath ?? this.toJsonPath(r.path, referencedResource)) === '$.id',
    );
    return idRow ? { referenceLookupTable: idRow.target, referenceLookupKeyColumn: idRow.column } : {};
  }

  // ── graph helpers ───────────────────────────────────────────────────────────
  private isSourceNode(node: WorkflowNodeRequest): boolean {
    return node.category === 0 || node.category === 'Source';
  }

  private isDestinationNode(node: WorkflowNodeRequest): boolean {
    return node.nodeType.endsWith('DestinationNode');
  }

  private mappingNodeFeeding(
    destNodeId: string,
    graph: WorkflowBuildRequest,
  ): string | null {
    const edge = graph.edges.find((e) => e.toNodeId === destNodeId);
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
      current =
        graph.edges.find((e) => e.toNodeId === current)?.fromNodeId ?? null;
    }
    // Fallback: the first source in the graph (linear single-source pipelines).
    return [...sourceNodeIds][0] ?? null;
  }

  private fieldsFor(
    nodeId: string,
    nodesById: Map<string, WorkflowNodeRequest>,
  ): Record<string, string> {
    // Prefer the live store node fields; fall back to the serialized config on the graph node.
    const storeNode = this.store.byId(nodeId);
    if (storeNode) return storeNode.fields;
    const node = nodesById.get(nodeId);
    if (!node?.configurationJson) return {};
    try {
      const parsed = JSON.parse(node.configurationJson) as Record<
        string,
        unknown
      >;
      return Object.fromEntries(
        Object.entries(parsed).map(([k, v]) => [
          k,
          typeof v === 'string' ? v : JSON.stringify(v),
        ]),
      );
    } catch {
      return {};
    }
  }

  private slug(value: string): string {
    return (
      value
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 24) || 'dest'
    );
  }

  private shortId(): string {
    return Math.random().toString(36).slice(2, 8);
  }
}
