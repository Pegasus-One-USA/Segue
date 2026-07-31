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

/**
 * Non-secret dest_* fields as a flat JSON object — everything getConfig()/buildDestination() collect EXCEPT
 * dest_password/dest_sftpPassword, which only ever live in the encrypted secret (see buildSqlConnectionString/
 * buildSftpUri above), never here. Persisted on DestinationConfiguration.ConnectionMetadataJson so a later
 * "select existing" can repopulate a form's non-secret fields without ever reading the secret back.
 */
export function buildConnectionMetadata(f: Record<string, string>, isSql: boolean): string {
  const keys = isSql
    ? ['dest_name', 'dest_engine', 'dest_server', 'dest_database', 'dest_auth', 'dest_username', 'dest_schema', 'dest_writeMode', 'dest_requireSsl']
    : ['dest_name', 'dest_deliveryMode', 'dest_filePattern', 'dest_delimiter', 'dest_encoding',
       'dest_sftpHost', 'dest_sftpPort', 'dest_sftpUsername', 'dest_sftpAuthType', 'dest_sftpRemoteFolder',
       'dest_emailTo', 'dest_emailCc', 'dest_emailSubjectTemplate', 'dest_emailBodyTemplate',
       'dest_downloadLinkExpiryMinutes'];
  const metadata: Record<string, string> = {};
  for (const key of keys) {
    if (f[key] !== undefined) metadata[key] = f[key];
  }
  return JSON.stringify(metadata);
}
