import { Transform } from '../models/transform.model';

export const TRANSFORMS: Transform[] = [
  { id: 'fhir-validation',  rank: 2, name: 'FHIR Validation',                     sub: 'Validate resources against US Core / base R4 profiles.' },
  { id: 'normalize',        rank: 3, group: 'normalize', name: 'Normalize Data',          sub: 'Flatten extensions, score quality, tag US Core.' },
  { id: 'patient-matching', rank: 3, group: 'normalize', name: 'Patient Matching (MPI)',  sub: 'Match patients against a master patient index.' },
  { id: 'merge-patients',   rank: 3, group: 'normalize', name: 'Merge Patients',          sub: 'Merge duplicate patient records.' },
  { id: 'terminology',      rank: 4, name: 'Terminology Mapping',                  sub: 'Validate / translate ICD, SNOMED, LOINC, RxNorm codes.' },
  { id: 'deid-safeharbor',  rank: 5, group: 'deid', name: 'De-identification · Safe Harbor', sub: 'Per-resource HIPAA Safe Harbor redaction.' },
  { id: 'deid-kanon',       rank: 5, group: 'deid', name: 'De-identification · k-anonymity',  sub: 'Cohort generalization + suppression.' },
  { id: 'field-mapping',    rank: 6, name: 'Field Mapping',                        sub: 'Map FHIR paths to destination fields.' },
  { id: 'dest-sqlserver',   rank: 7, category: 'Relational',   destinationType: 'SqlServer',      name: 'SQL Server',         sub: 'Write to Microsoft SQL Server.',    permissionPrefix: 'sqlserver' },
  { id: 'dest-azuresql',    rank: 7, category: 'Relational',   destinationType: 'AzureSql',       name: 'Azure SQL',          sub: 'Write to Azure SQL Database.',      permissionPrefix: 'azuresql' },
  { id: 'dest-postgres',    rank: 7, category: 'Relational',   destinationType: 'PostgreSql',     name: 'PostgreSQL',         sub: 'Write to PostgreSQL.',              permissionPrefix: 'postgresql' },
  { id: 'dest-mysql',       rank: 7, category: 'Relational',   destinationType: 'MySql',          name: 'MySQL',              sub: 'Write to MySQL.',                   permissionPrefix: 'mysql' },
  { id: 'dest-mongo',       rank: 7, category: 'NoSQL',        destinationType: 'Mongo',          name: 'MongoDB',            sub: 'Write to a MongoDB collection.',    permissionPrefix: 'mongo' },
  // The 15 rows below (Snowflake ... In-memory) are DestinationType values with no dedicated
  // PermissionGroupCode of their own — SourceSystemPermissionGroups.GroupFor falls back to the
  // generic SourceConnections group for every one of them, and the backend's
  // ControllerAuthorizationExtensions.HasPermissionAsync uses that exact fallback when a real
  // create/edit/delete request for one of these types comes in. So `sourceconnections` here isn't a
  // placeholder — it's the real code the backend already checks; leaving permissionPrefix unset (as
  // these previously were) made the tile always-visible and its create/edit check a silent no-op.
  { id: 'dest-snowflake',   rank: 7, category: 'Analytics',    destinationType: 'Snowflake',      name: 'Snowflake',          sub: 'Load into Snowflake.',              permissionPrefix: 'sourceconnections' },
  { id: 'dest-powerbi',     rank: 7, category: 'Analytics',    destinationType: 'PowerBi',        name: 'Power BI',           sub: 'Push to a Power BI dataset.',       permissionPrefix: 'sourceconnections' },
  { id: 'dest-tableau',     rank: 7, category: 'Analytics',    destinationType: 'Tableau',        name: 'Tableau',            sub: 'Publish to Tableau.',               permissionPrefix: 'sourceconnections' },
  { id: 'dest-databricks',  rank: 7, category: 'Analytics',    destinationType: 'Databricks',     name: 'Databricks',         sub: 'Load into Databricks.',             permissionPrefix: 'sourceconnections' },
  { id: 'dest-blob',        rank: 7, category: 'Cloud / FHIR', destinationType: 'BlobStorage',    name: 'Azure Blob Storage', sub: 'Write objects to Azure Blob.',      permissionPrefix: 'blobstorage' },
  { id: 'dest-datalake-webhook', rank: 7, category: 'Cloud / FHIR', destinationType: 'DataLakeWebhook', name: 'Data Lake Webhook', sub: 'Push batched records to a lake ingestion endpoint.', permissionPrefix: 'sourceconnections' },
  { id: 'dest-fabric',      rank: 7, category: 'Cloud / FHIR', destinationType: 'DataFabricAzure', name: 'Microsoft Fabric', sub: 'Land files in a Fabric Lakehouse (OneLake).', permissionPrefix: 'sourceconnections' },
  { id: 'dest-s3',          rank: 7, category: 'Cloud / FHIR', destinationType: 'S3',             name: 'Amazon S3',          sub: 'Write objects to Amazon S3.',       permissionPrefix: 'sourceconnections' },
  { id: 'dest-fhir',        rank: 7, category: 'Cloud / FHIR', destinationType: 'FhirRepository', name: 'Aidbox',             sub: 'POST a transaction bundle to a FHIR store.', permissionPrefix: 'sourceconnections' },
  { id: 'dest-medplum',     rank: 7, category: 'Cloud / FHIR', destinationType: 'Medplum',        name: 'Medplum (FHIR)',     sub: 'Write FHIR resources to a Medplum store', permissionPrefix: 'sourceconnections' },
  { id: 'dest-azurefhir',   rank: 7, category: 'Cloud / FHIR', destinationType: 'AzureFhirService', name: 'Azure FHIR Service', sub: 'Write FHIR resources to Azure Health Data Services.', permissionPrefix: 'sourceconnections' },
  { id: 'dest-csv',         rank: 7, category: 'File',         destinationType: 'Csv',            name: 'CSV',                sub: 'Emit CSV files.',                   permissionPrefix: 'csv' },
  { id: 'dest-xlsx',        rank: 7, category: 'File',         destinationType: 'Excel',          name: 'Excel',              sub: 'Emit .xlsx workbooks.',             permissionPrefix: 'sourceconnections' },
  { id: 'dest-ndjson',      rank: 7, category: 'File',         destinationType: 'Ndjson',         name: 'NDJSON',             sub: 'Emit newline-delimited JSON.',      permissionPrefix: 'sourceconnections' },
  { id: 'dest-parquet',     rank: 7, category: 'File',         destinationType: 'Parquet',        name: 'Parquet',            sub: 'Emit columnar Parquet.',            permissionPrefix: 'sourceconnections' },
  { id: 'dest-avro',        rank: 7, category: 'File',         destinationType: 'Avro',           name: 'Avro',               sub: 'Emit Avro records.',                permissionPrefix: 'sourceconnections' },
  { id: 'dest-protobuf',    rank: 7, category: 'File',         destinationType: 'Protobuf',       name: 'Protobuf',           sub: 'Emit Protobuf messages.',           permissionPrefix: 'sourceconnections' },
  { id: 'dest-pdf',         rank: 7, category: 'File',         destinationType: 'Pdf',            name: 'PDF Report',         sub: 'Render a PDF report.',              permissionPrefix: 'sourceconnections' },
  { id: 'dest-sftp',        rank: 7, category: 'Delivery',     destinationType: 'Sftp',           name: 'SFTP',               sub: 'Deliver files over SFTP.',          permissionPrefix: 'sftp' },
  { id: 'dest-restapi',     rank: 7, category: 'Delivery',     destinationType: 'RestApi',        name: 'REST API',           sub: 'POST to an outbound REST endpoint.', permissionPrefix: 'sourceconnections' },
  { id: 'dest-apiendpoint', rank: 7, category: 'Delivery',     destinationType: 'ApiEndpoint',     name: 'API Endpoint',       sub: 'Fully configurable outbound API — auth, batching, retry.', permissionPrefix: 'sourceconnections' },
  { id: 'dest-inmemory',    rank: 7, category: 'Delivery',     destinationType: 'InMemory',       name: 'In-memory (test)',   sub: 'Sink for testing — discards output.', permissionPrefix: 'sourceconnections' },
  { id: 'audit-lineage',    rank: 8, category: 'Audit & Lineage', name: 'Audit & Lineage', sub: 'Hash-chained audit + record-level lineage.' },
  { id: 'hedis',            rank: 9, category: 'Analytics',    name: 'HEDIS Measure Report', sub: 'Compute HEDIS quality measures.' },
  { id: 'anomaly',          rank: 9, category: 'Analytics',    name: 'Anomaly Detection',  sub: 'Flag statistical anomalies.' },
  { id: 'patient-agg',      rank: 9, category: 'Analytics',    name: 'Patient Aggregation', sub: 'Aggregate a patient-360 view.' },
];

export const REPEATABLE_TRANSFORMS = new Set(['field-mapping']);
