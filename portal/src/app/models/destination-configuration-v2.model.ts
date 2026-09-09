/** V2's own independent copy of destination-connections/models/destination-configuration.model.ts — V2
 *  never imports a V1-owned model file, so these DTO/type shapes are deliberately duplicated (kept
 *  member-for-member identical to the backend contract, same as the original) rather than shared. */
export type DestinationTypeV2 =
  | 'InMemory'
  | 'SqlServer'
  | 'AzureSql'
  | 'RestApi'
  | 'BlobStorage'
  | 'Csv'
  | 'Excel'
  | 'PowerBi'
  | 'Snowflake'
  | 'FhirRepository'
  | 'PostgreSql'
  | 'MySql'
  | 'S3'
  | 'Ndjson'
  | 'Parquet'
  | 'Sftp'
  | 'Tableau'
  | 'Pdf'
  | 'Avro'
  | 'Protobuf'
  | 'Databricks'
  | 'Mongo'
  | 'Medplum'
  | 'AzureFhirService'
  | 'DataLakeWebhook'
  | 'DataFabricAzure';

/** SQL-family destinations are the only ones Mapping applies to in the V2 chain (Source → Destination →
 *  [Mapping →] Transformation → De-identification). "NoSQL" in the product spec maps to Mongo — the only
 *  NoSQL-shaped destination type today. */
export const SQL_FAMILY_DESTINATION_TYPES: ReadonlySet<DestinationTypeV2> = new Set<DestinationTypeV2>([
  'SqlServer', 'AzureSql', 'PostgreSql', 'MySql', 'Mongo',
]);

export type ArtifactDeliveryMode = 'download' | 'email' | 'sftp' | 'downloadUrl';

export interface DestinationConfigurationDto {
  id: string;
  name: string;
  destinationType: DestinationTypeV2;
  keyVaultName: string;
  secretName: string;
  target: string | null;
  isEnabled: boolean;
  connectionMetadataJson?: string | null;
  createdOnUtc?: string | null;
  createdBy?: string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?: string | null;
  deIdentificationProfileId?: string | null;
}

export interface CreateDestinationConfigurationRequest {
  name: string;
  destinationType: DestinationTypeV2;
  keyVaultName: string;
  secretName: string;
  target?: string | null;
  inlineSecret?: string | null;
  connectionMetadataJson?: string | null;
  deIdentificationProfileId?: string | null;
  /** Set when this request forks a brand-new connection off an existing one the user picked but then edited
   *  (the wizard never mutates a shared connection in place) — lets the backend resolve THIS destination's own
   *  already-stored secret and inherit its credentials into inlineSecret when the latter is missing them (e.g.
   *  the user only toggled "Require SSL" and never intended to change the password). SQL-family destinations
   *  only; ignored for every other type. */
  inheritSecretFromDestinationId?: string | null;
}

export interface DeIdentificationProfileDto {
  id: string;
  name: string;
  description?: string | null;
}

export interface CreateDeIdentificationProfileRequest {
  name: string;
  description?: string | null;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export type DestinationSortColumn = 'name' | 'destinationType' | 'target' | 'isEnabled' | 'actionOn';
export type SortOrder = 'asc' | 'desc';

export interface DestinationConfigurationFilter {
  search?: string;
  destinationType?: DestinationTypeV2;
  isEnabled?: boolean;
  sortBy?: DestinationSortColumn;
  sortOrder?: SortOrder;
  page: number;
  pageSize: number;
}
