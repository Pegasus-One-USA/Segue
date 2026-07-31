/** Must match the backend's DestinationType enum member names (serialized as strings). This screen only
 * exposes Sql/CSV/Mongo creation (matching the workflow wizard), but Type filtering accepts any of the 22
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
  | 'Mongo';

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
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export type DestinationSortColumn = 'name' | 'destinationType' | 'target' | 'isEnabled';
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
