/** Must match the backend's DestinationType enum member names (serialized as strings). This screen only
 * exposes Sql/CSV/Mongo creation (matching the workflow wizard), but Type filtering accepts any of the 23
 * values an existing row could have been created as (e.g. by a future wizard extension). */
export type DestinationType =
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
  | 'DataFabricAzure'
  /** Microsoft Fabric Warehouse — see DestinationTypeV2's own note; kept in step with that union and with the
   *  backend DestinationType enum, which all three must agree on. */
  | 'DataFabricWarehouse'
  | 'ApiEndpoint';

/** Must match the backend's ArtifactDeliveryMode enum member names. Stored as `dest_deliveryMode` in
 *  ConnectionMetadataJson for Csv destinations — replaces the old `dest_storageType` field. */
export type ArtifactDeliveryMode = 'download' | 'email' | 'sftp' | 'downloadUrl';

/** Matches the API's DestinationConfigurationDto shape exactly, so no DTO↔model mapping is needed. */
export interface DestinationConfigurationDto {
  id: string;
  name: string;
  destinationType: DestinationType;
  keyVaultName: string;
  secretName: string;
  target: string | null;
  isEnabled: boolean;
  /** Non-secret dest_* fields as a JSON string (server/database/schema/... for SQL; folder/sftpHost/... for
   *  CSV/SFTP) — see DestinationConfiguration.ConnectionMetadataJson. Never carries a password. */
  connectionMetadataJson?: string | null;
  createdOnUtc?: string | null;
  createdBy?: string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?: string | null;
  /** Which DeIdentificationProfile applies to this destination — null means no de-identification. */
  deIdentificationProfileId?: string | null;
}

export interface CreateDestinationConfigurationRequest {
  name: string;
  destinationType: DestinationType;
  keyVaultName: string;
  secretName: string;
  target?: string | null;
  /** When set, the raw connection secret is provisioned encrypted at (keyVaultName, secretName) server-side. */
  inlineSecret?: string | null;
  /** Non-secret connection fields as a JSON string — see DestinationConfigurationDto.connectionMetadataJson. */
  connectionMetadataJson?: string | null;
  /** Which DeIdentificationProfile applies to this destination — null/omitted means no de-identification. */
  deIdentificationProfileId?: string | null;
  /** Set when this request forks a brand-new connection off an existing one the user picked but then edited
   *  (the wizard never mutates a shared connection in place) — lets the backend resolve THIS destination's own
   *  already-stored secret and inherit its credentials into inlineSecret when the latter is missing them (e.g.
   *  the user only toggled "Require SSL" and never intended to change the password). SQL-family destinations
   *  only; ignored for every other type. */
  inheritSecretFromDestinationId?: string | null;
}

/** A named, reusable group of pre-mapping de-identification rules — see DeIdentificationProfile (backend). */
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
  destinationType?: DestinationType;
  isEnabled?: boolean;
  sortBy?: DestinationSortColumn;
  sortOrder?: SortOrder;
  page: number;
  pageSize: number;
}
