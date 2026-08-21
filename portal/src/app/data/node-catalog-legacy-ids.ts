// data/node-catalog-legacy-ids.ts
//
// The one remaining hand-maintained mapping in this area — and deliberately the ONLY one: it links
// the pre-existing canvas node id space ('epic', 'dest-sqlserver', 'dest-fhir', ...) to the Node
// Catalog's canonical (kind, type) identity. It carries NO display name, permission, category, or
// rollout metadata — all of that now comes from the backend catalog (NodeCatalogService). This
// mapping exists only because canvas node `transformId`/source-form-key strings are already
// persisted in existing saved workflows (WorkflowNodeConfigurations, Mapping JSON) — renaming them
// to the catalog's canonical enum names would be a breaking data migration, which the Node Catalog
// consolidation was explicitly told to avoid. Node Library / Workflow Builder still emit and store
// these same ids exactly as before; only their RBAC/display metadata is now catalog-sourced.
//
// Rank-2..6,8,9 pipeline steps (field-mapping, normalize, terminology, de-identification, audit,
// analytics, ...) have no entry here — they have no SourceSystemType/DestinationType backing at
// all, and stay entirely outside the Node Catalog (still governed by transforms.data.ts +
// PhaseConfigService.hiddenRanks, unchanged).

export const SOURCE_ID_TO_TYPE: Readonly<Record<string, string>> = {
  epic: 'Epic',
  cerner: 'Cerner',
  athena: 'Athenahealth',
  allscripts: 'Allscripts',
  healow: 'Healow',
  meditech: 'MeditechGreenfield',
  'generic-fhir': 'GenericFhir',
  hl7v2: 'Hl7v2',
  sample: 'Sample',
};

export const DESTINATION_ID_TO_TYPE: Readonly<Record<string, string>> = {
  'dest-sqlserver': 'SqlServer',
  'dest-azuresql': 'AzureSql',
  'dest-postgres': 'PostgreSql',
  'dest-mysql': 'MySql',
  'dest-mongo': 'Mongo',
  'dest-snowflake': 'Snowflake',
  'dest-powerbi': 'PowerBi',
  'dest-tableau': 'Tableau',
  'dest-databricks': 'Databricks',
  'dest-blob': 'BlobStorage',
  'dest-s3': 'S3',
  'dest-fhir': 'FhirRepository',
  'dest-medplum': 'Medplum',
  'dest-csv': 'Csv',
  'dest-xlsx': 'Excel',
  'dest-ndjson': 'Ndjson',
  'dest-parquet': 'Parquet',
  'dest-avro': 'Avro',
  'dest-protobuf': 'Protobuf',
  'dest-pdf': 'Pdf',
  'dest-sftp': 'Sftp',
  'dest-restapi': 'RestApi',
  'dest-inmemory': 'InMemory',
};
