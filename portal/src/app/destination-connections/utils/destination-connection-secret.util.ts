/**
 * Builds the opaque connection secret (SQL connection string / sftp:// URI) from a destination connection
 * form's dest_* config bag, and a fresh (keyVaultName, secretName) pair for a brand-new connection. Mirrors
 * WorkflowApiService's workflow-build-assembler.service.ts buildSqlConnectionString/buildSftpUri/slug/shortId —
 * duplicated rather than imported because those are private members of a service built for a different call
 * site (assembling a whole workflow graph), not a small reusable util.
 */
export function slugForSecretName(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 24) || 'dest';
}

export function shortSecretSuffix(): string {
  return Math.random().toString(36).slice(2, 8);
}

export function newSecretName(destinationName: string): string {
  return `dest-${slugForSecretName(destinationName)}-${shortSecretSuffix()}`;
}

export function buildSqlConnectionString(f: Record<string, string>): string {
  const engine = f['dest_engine'] ?? 'sqlserver';
  const server = f['dest_server'] ?? '';
  const database = f['dest_database'] ?? '';
  const requireSsl = f['dest_requireSsl'] === 'true';

  if (engine === 'postgres') {
    // "Require" mode encrypts without validating the server certificate — no separate "trust cert" flag needed.
    // Defaults to Prefer (off) so a local/docker Postgres with SSL disabled still connects; check the SSL
    // toggle for providers that enforce it (e.g. AWS RDS's rds.force_ssl).
    return [`Host=${server}`, `Database=${database}`, `Username=${f['dest_username'] ?? ''}`, `Password=${f['dest_password'] ?? ''}`, `SSL Mode=${requireSsl ? 'Require' : 'Prefer'}`].join(';');
  }
  if (engine === 'mysql') {
    return [`Server=${server}`, `Database=${database}`, `User Id=${f['dest_username'] ?? ''}`, `Password=${f['dest_password'] ?? ''}`, `SslMode=${requireSsl ? 'Required' : 'Preferred'}`].join(';');
  }

  const parts = [`Server=${server}`, `Database=${database}`];
  if ((f['dest_auth'] ?? 'sql-auth') === 'sql-auth') {
    parts.push(`User Id=${f['dest_username'] ?? ''}`, `Password=${f['dest_password'] ?? ''}`);
  } else {
    parts.push('Authentication=Active Directory Default');
  }
  parts.push('TrustServerCertificate=True', 'Encrypt=True');
  return parts.join(';');
}

export function buildSftpUri(f: Record<string, string>): string {
  const user = encodeURIComponent(f['dest_sftpUsername'] ?? '');
  const pass = encodeURIComponent(f['dest_sftpPassword'] ?? '');
  const host = f['dest_sftpHost'] ?? '';
  const port = f['dest_sftpPort'] ?? '22';
  const folder = (f['dest_sftpRemoteFolder'] ?? f['dest_folder'] ?? '').replace(/^\/+/, '');
  return `sftp://${user}:${pass}@${host}:${port}/${folder}`;
}

/** Builds the FHIR-repository destination's encrypted secret blob, shaped to match exactly what the backend's
 *  FhirRepositoryAuthResolver (FHIRBridge.Infrastructure) expects to parse for each auth type. Mirrors
 *  WorkflowBuildAssemblerService's private buildFhirSecretBlob — duplicated rather than imported for the same
 *  reason buildSqlConnectionString/buildSftpUri above are (see this file's header comment). */
export function buildFhirSecretBlob(f: Record<string, string>): string {
  const authType = f['dest_authType'] ?? 'oauth2';
  if (authType === 'basic') {
    return JSON.stringify({ username: f['dest_username'] ?? '', password: f['dest_password'] ?? '' });
  }
  if (authType === 'bearer') {
    return JSON.stringify({ token: f['dest_bearerToken'] ?? '' });
  }
  // Managed identity (Azure FHIR Service) never resolves a Key Vault secret at all — see
  // FhirRepositoryAuthResolver's "managedidentity" branch, which skips secret retrieval entirely for it.
  if (authType === 'managedIdentity') {
    return '';
  }
  return JSON.stringify({
    clientId: f['dest_clientId'] ?? '',
    clientSecret: f['dest_clientSecret'] ?? '',
    tokenEndpoint: f['dest_tokenEndpoint'] ?? '',
    // Only AzureFhirServiceDestinationFormComponent's config carries dest_fhirAzureScope (even blank) — the
    // generic Aidbox/FhirRepository form never collects a scope at all, and its OAuth2 servers have tolerated
    // an omitted scope, so this is deliberately scoped to Azure FHIR Service's config shape only. Entra ID's
    // v2.0 token endpoint requires a non-empty scope for client_credentials (AADSTS90014 otherwise) — default
    // to Azure's own {resource}/.default convention when the user left the override blank.
    ...(f['dest_fhirAzureScope'] !== undefined
      ? { scope: f['dest_fhirAzureScope'] || `${(f['dest_baseUrl'] ?? '').replace(/\/+$/, '')}/.default` }
      : {}),
  });
}

/**
 * Non-secret dest_* fields as a flat JSON object — everything getConfig()/buildDestination() collect EXCEPT
 * dest_password/dest_sftpPassword/dest_clientSecret/dest_bearerToken, which only ever live in the encrypted
 * secret (see buildSqlConnectionString/buildSftpUri/buildFhirSecretBlob above), never here. Persisted on
 * DestinationConfiguration.ConnectionMetadataJson so a later "select existing" can repopulate a form's
 * non-secret fields without ever reading the secret back.
 */
export function buildConnectionMetadata(
  f: Record<string, string>,
  kind: 'sql' | 'csv' | 'fhir' | 'blob' | 'datalake' | 'fabric',
): string {
  const keys =
    // Data Lake Webhook — every field here is non-secret transport/framing configuration. The credential
    // (bearer token / API key / "user:password" / HMAC shared secret / OAuth2 client secret) is dest_dlwSecret
    // and is deliberately absent from this list: it only ever lives in the encrypted secret. Note that
    // dest_dlwEndpointUrl IS carried here even though it also becomes DestinationConfiguration.target, for the
    // same reason dest_medplumBaseUrl is — the workflow-graph run path can reconstruct the destination with an
    // empty Target, and DataLakeWebhookSettings.Parse falls back to this key.
    kind === 'datalake'
      ? ['dest_name', 'dest_dlwEndpointUrl', 'dest_dlwAuthMode', 'dest_dlwAuthHeaderName',
         'dest_dlwSignatureHeaderName', 'dest_dlwTimestampHeaderName',
         'dest_dlwTokenEndpoint', 'dest_dlwClientId', 'dest_dlwScope',
         'dest_dlwPayloadShape', 'dest_dlwHttpMethod', 'dest_dlwContentType', 'dest_dlwCompression',
         'dest_dlwBatchSize', 'dest_dlwMaxRequestBytes', 'dest_dlwTimeoutSeconds',
         'dest_dlwRetryCount', 'dest_dlwRetryBackoffSeconds', 'dest_dlwExpectedStatusCodes',
         'dest_dlwHeadersJson', 'dest_dlwIncludeSourceJson', 'dest_dlwOnFailure']
    // Microsoft Fabric (OneLake) — workspace/item/path/format/partitioning plus the Entra identity's non-secret
    // parts. The service-principal client secret is dest_fabricSecret and never appears here; managed-identity
    // mode has no secret at all (see FabricDestinationSettings.RequiresSecret).
    : kind === 'fabric'
      ? ['dest_name', 'dest_fabricMode', 'dest_fabricWorkspace', 'dest_fabricItemName', 'dest_fabricItemType',
         'dest_fabricPath', 'dest_fabricFileFormat', 'dest_fabricPartitionBy',
         'dest_fabricAuthMode', 'dest_fabricTenantId', 'dest_fabricClientId',
         'dest_fabricManagedIdentityClientId', 'dest_fabricEndpointSuffix', 'dest_fabricAuthorityHost',
         'dest_fabricAccountUrl']
    : kind === 'sql'
      ? ['dest_name', 'dest_engine', 'dest_server', 'dest_database', 'dest_auth', 'dest_username', 'dest_schema', 'dest_writeMode', 'dest_requireSsl']
      : kind === 'fhir'
        ? ['dest_name', 'dest_baseUrl', 'dest_project', 'dest_writeMode', 'dest_fhirWriteMode',
           'dest_tokenEndpoint', 'dest_clientId', 'dest_username',
           // Azure FHIR Service (Azure Health Data Services) — non-secret managed-identity/scope overrides.
           // dest_fhirAuthType itself is set separately, via the dest_authType bridge below.
           'dest_fhirAzureScope', 'dest_fhirManagedIdentityClientId',
           'dest_autoFetchMissingReferences', 'dest_autoFetchMaxCount']
        : kind === 'blob'
          ? ['dest_name', 'dest_blobAuthMode', 'dest_blobContainer', 'dest_blobAccountUrl', 'dest_blobAccountName',
             'dest_blobEndpointSuffix', 'dest_blobTenantId', 'dest_blobClientId', 'dest_blobManagedIdentityClientId',
             'dest_blobPathPrefix', 'dest_blobCreateContainerIfNotExists',
             // Two independent settings: how many records share one blob (bulk vs individual), and — only
             // meaningful for individual — what happens relative to a record's existing blob (insert/upsert/update).
             'dest_blobGranularity', 'dest_blobRecordMode',
             // Only meaningful for individual delivery — folder/file-name placeholder patterns (see
             // BlobDestinationSettings.FolderPattern/FileNamePattern). Blank means "use the record mode's default".
             'dest_blobFolderPattern', 'dest_blobFileNamePattern']
        : ['dest_name', 'dest_deliveryMode', 'dest_filePattern', 'dest_delimiter', 'dest_encoding',
           'dest_sftpHost', 'dest_sftpPort', 'dest_sftpUsername', 'dest_sftpAuthType', 'dest_sftpRemoteFolder',
           'dest_emailTo', 'dest_emailCc', 'dest_emailSubjectTemplate', 'dest_emailBodyTemplate',
           'dest_downloadLinkExpiryMinutes',
           // MongoDB — the connection string itself lives only in the encrypted secret (see the mongo branch
           // in destination-wizard.component.ts's provisionDestinationConnection); collection/writeMode aren't
           // secret, so they round-trip here the same way SQL's non-secret fields do.
           'dest_collection', 'dest_writeMode',
           // Medplum (FHIR) — the base URL becomes the DestinationConfiguration.target and the client secret /
           // PEM key becomes the encrypted inlineSecret (dest_medplumSecret, redacted); everything else is
           // non-secret connection metadata that round-trips here.
           // Base URL also carried in metadata (not only Target): the workflow-graph run path can reconstruct the
           // destination with an empty Target, so the Medplum writer falls back to this. See MedplumConnectionMetadata.BaseUrl.
           'dest_medplumBaseUrl',
           'dest_medplumClientId', 'dest_medplumAuthMethod', 'dest_medplumWriteMode',
           'dest_medplumBatchSize', 'dest_medplumIdentifierSystem',
           // FHIR Repository (plain FHIR R4 server, e.g. HAPI) — no auth: the base URL becomes the
           // DestinationConfiguration.target and there is no secret at all. Base URL also carried here so the
           // workflow-graph run path can reconstruct the destination with an empty Target.
           'dest_fhirBaseUrl'];
  const metadata: Record<string, string> = {};
  for (const key of keys) {
    if (f[key] !== undefined) metadata[key] = f[key];
  }
  // The backend reads this metadata key as dest_fhirAuthType (see FhirRepositoryAuthResolver); the form's own
  // field/control name is dest_authType — bridge the naming difference here, matching
  // WorkflowBuildAssemblerService.buildConnectionMetadata's own established bridge exactly. The form's internal
  // value for OAuth2 is 'oauth2' (matches its authType control/validators), but the backend's
  // CreateDestinationConfigurationRequestValidator/FhirRepositoryAuthResolver only recognize 'clientCredentials'.
  if (kind === 'fhir' && f['dest_authType'] !== undefined) {
    metadata['dest_fhirAuthType'] = f['dest_authType'] === 'oauth2' ? 'clientCredentials' : f['dest_authType'];
  }
  return JSON.stringify(metadata);
}
