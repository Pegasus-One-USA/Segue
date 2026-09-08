import { Transform } from '../models/transform-v2.model';

/** V2's simplified straight-chain catalog: Source(0) → Destination(1) → Mapping(2) → Transformation(3) →
 *  De-identification(4). Mapping only applies when the destination is SQL-family (see
 *  SQL_FAMILY_DESTINATION_TYPES in destination-configuration-v2.model.ts); Transformation and
 *  De-identification apply to every destination type. The old granular rank-3/4/5 steps (normalize,
 *  patient-matching, merge-patients, terminology, deid-safeharbor, deid-kanon) are collapsed into the
 *  single 'transformation' and 'deidentification' entries below — each still backed by the same
 *  transform-rules-dialog/rule-config-form config UI those steps always used (see field-mapping/). */
export const TRANSFORMS: Transform[] = [
  { id: 'dest-sqlserver',   rank: 1, category: 'Relational',   destinationType: 'SqlServer',      name: 'SQL Server',         sub: 'Write to Microsoft SQL Server.',    permissionPrefix: 'sqlserver' },
  { id: 'dest-azuresql',    rank: 1, category: 'Relational',   destinationType: 'AzureSql',       name: 'Azure SQL',          sub: 'Write to Azure SQL Database.',      permissionPrefix: 'azuresql' },
  { id: 'dest-postgres',    rank: 1, category: 'Relational',   destinationType: 'PostgreSql',     name: 'PostgreSQL',         sub: 'Write to PostgreSQL.',              permissionPrefix: 'postgresql' },
  { id: 'dest-mysql',       rank: 1, category: 'Relational',   destinationType: 'MySql',          name: 'MySQL',              sub: 'Write to MySQL.',                   permissionPrefix: 'mysql' },
  { id: 'dest-mongo',       rank: 1, category: 'NoSQL',        destinationType: 'Mongo',          name: 'MongoDB',            sub: 'Write to a MongoDB collection.',    permissionPrefix: 'mongo' },
  // The 15 rows below (Snowflake ... In-memory) are DestinationType values with no dedicated
  // PermissionGroupCode of their own — SourceSystemPermissionGroups.GroupFor falls back to the
  // generic SourceConnections group for every one of them, and the backend's
  // ControllerAuthorizationExtensions.HasPermissionAsync uses that exact fallback when a real
  // create/edit/delete request for one of these types comes in. So `sourceconnections` here isn't a
  // placeholder — it's the real code the backend already checks; leaving permissionPrefix unset (as
  // these previously were) made the tile always-visible and its create/edit check a silent no-op.
  { id: 'dest-snowflake',   rank: 1, category: 'Analytics',    destinationType: 'Snowflake',      name: 'Snowflake',          sub: 'Load into Snowflake.',              permissionPrefix: 'sourceconnections' },
  { id: 'dest-powerbi',     rank: 1, category: 'Analytics',    destinationType: 'PowerBi',        name: 'Power BI',           sub: 'Push to a Power BI dataset.',       permissionPrefix: 'sourceconnections' },
  { id: 'dest-tableau',     rank: 1, category: 'Analytics',    destinationType: 'Tableau',        name: 'Tableau',            sub: 'Publish to Tableau.',               permissionPrefix: 'sourceconnections' },
  { id: 'dest-databricks',  rank: 1, category: 'Analytics',    destinationType: 'Databricks',     name: 'Databricks',         sub: 'Load into Databricks.',             permissionPrefix: 'sourceconnections' },
  { id: 'dest-blob',        rank: 1, category: 'Cloud / FHIR', destinationType: 'BlobStorage',    name: 'Azure Blob Storage', sub: 'Write objects to Azure Blob.',      permissionPrefix: 'blobstorage' },
  { id: 'dest-s3',          rank: 1, category: 'Cloud / FHIR', destinationType: 'S3',             name: 'Amazon S3',          sub: 'Write objects to Amazon S3.',       permissionPrefix: 'sourceconnections' },
  { id: 'dest-fhir',        rank: 1, category: 'Cloud / FHIR', destinationType: 'FhirRepository', name: 'Aidbox',             sub: 'POST a transaction bundle to a FHIR store.', permissionPrefix: 'fhirrepository' },
  { id: 'dest-medplum',     rank: 1, category: 'Cloud / FHIR', destinationType: 'Medplum',        name: 'Medplum (FHIR)',     sub: 'Write FHIR resources to a Medplum store', permissionPrefix: 'medplum' },
  { id: 'dest-azurefhir',   rank: 1, category: 'Cloud / FHIR', destinationType: 'AzureFhirService', name: 'Azure FHIR Service', sub: 'Write FHIR resources to Azure Health Data Services.', permissionPrefix: 'azurefhirservice' },
  { id: 'dest-csv',         rank: 1, category: 'File',         destinationType: 'Csv',            name: 'CSV',                sub: 'Emit CSV files.',                   permissionPrefix: 'csv' },
  { id: 'dest-xlsx',        rank: 1, category: 'File',         destinationType: 'Excel',          name: 'Excel',              sub: 'Emit .xlsx workbooks.',             permissionPrefix: 'sourceconnections' },
  { id: 'dest-ndjson',      rank: 1, category: 'File',         destinationType: 'Ndjson',         name: 'NDJSON',             sub: 'Emit newline-delimited JSON.',      permissionPrefix: 'sourceconnections' },
  { id: 'dest-parquet',     rank: 1, category: 'File',         destinationType: 'Parquet',        name: 'Parquet',            sub: 'Emit columnar Parquet.',            permissionPrefix: 'sourceconnections' },
  { id: 'dest-avro',        rank: 1, category: 'File',         destinationType: 'Avro',           name: 'Avro',               sub: 'Emit Avro records.',                permissionPrefix: 'sourceconnections' },
  { id: 'dest-protobuf',    rank: 1, category: 'File',         destinationType: 'Protobuf',       name: 'Protobuf',           sub: 'Emit Protobuf messages.',           permissionPrefix: 'sourceconnections' },
  { id: 'dest-pdf',         rank: 1, category: 'File',         destinationType: 'Pdf',            name: 'PDF Report',         sub: 'Render a PDF report.',              permissionPrefix: 'sourceconnections' },
  { id: 'dest-sftp',        rank: 1, category: 'Delivery',     destinationType: 'Sftp',           name: 'SFTP',               sub: 'Deliver files over SFTP.',          permissionPrefix: 'sftp' },
  { id: 'dest-restapi',     rank: 1, category: 'Delivery',     destinationType: 'RestApi',        name: 'REST API',           sub: 'POST to an outbound REST endpoint.', permissionPrefix: 'sourceconnections' },
  { id: 'dest-inmemory',    rank: 1, category: 'Delivery',     destinationType: 'InMemory',       name: 'In-memory (test)',   sub: 'Sink for testing — discards output.', permissionPrefix: 'sourceconnections' },

  { id: 'field-mapping',    rank: 2, name: 'Mapping',           sub: 'Map FHIR paths to destination fields — SQL-family destinations only.' },
  { id: 'transformation',   rank: 3, name: 'Transformation',    sub: 'Apply transformation rules (normalize, terminology, patient matching, ...).' },
  { id: 'deidentification', rank: 4, name: 'De-identification', sub: 'Apply de-identification rules (Safe Harbor, k-anonymity, ...).' },
];

export const REPEATABLE_TRANSFORMS = new Set<string>();
