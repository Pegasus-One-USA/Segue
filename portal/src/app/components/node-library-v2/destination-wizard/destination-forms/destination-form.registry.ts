import { DestinationFormRegistry } from '../../../shared-v2/config-form/config-form-v2.contract';
import { DestinationTypeV2 as DestinationType } from '../../../../models/destination-configuration-v2.model';
import { SqlServerDestinationFormComponent } from './sql-server-destination-form.component';
import { AzureSqlDestinationFormComponent } from './azure-sql-destination-form.component';
import { MySqlDestinationFormComponent } from './my-sql-destination-form.component';
import { PostgreSqlDestinationFormComponent } from './postgre-sql-destination-form.component';
import { CsvDestinationFormComponent } from './csv-destination-form.component';
import { MongoDestinationFormComponent } from './mongo-destination-form.component';
import { SftpDestinationFormComponent } from './sftp-destination-form.component';
import { InMemoryDestinationFormComponent } from './in-memory-destination-form.component';
import { RestApiDestinationFormComponent } from './rest-api-destination-form.component';
import { BlobStorageDestinationFormComponent } from './blob-storage-destination-form.component';
import { ExcelDestinationFormComponent } from './excel-destination-form.component';
import { PowerBiDestinationFormComponent } from './power-bi-destination-form.component';
import { SnowflakeDestinationFormComponent } from './snowflake-destination-form.component';
import { FhirRepositoryDestinationFormComponent } from './fhir-repository-destination-form.component';
import { MedplumDestinationFormComponent } from './medplum-destination-form.component';
import { AzureFhirServiceDestinationFormComponent } from './azure-fhir-service-destination-form.component';
import { S3DestinationFormComponent } from './s3-destination-form.component';
import { NdjsonDestinationFormComponent } from './ndjson-destination-form.component';
import { ParquetDestinationFormComponent } from './parquet-destination-form.component';
import { TableauDestinationFormComponent } from './tableau-destination-form.component';
import { PdfDestinationFormComponent } from './pdf-destination-form.component';
import { AvroDestinationFormComponent } from './avro-destination-form.component';
import { ProtobufDestinationFormComponent } from './protobuf-destination-form.component';
import { DatabricksDestinationFormComponent } from './databricks-destination-form.component';
import { DataLakeWebhookDestinationFormComponent } from './data-lake-webhook-destination-form.component';
import { DataFabricDestinationFormComponent } from './data-fabric-destination-form.component';

/**
 * Single source of truth mapping every DestinationType to the standalone component that configures it —
 * replaces the hand-written `isSql()/isMongo()`/`toFormType()`-style branching that used to live separately
 * in DestinationWizardComponent and destination-connection-dialog.component.ts's toFormType(). Every
 * registered component implements DestinationConfigFormComponent (config-form.contract.ts); the SQL-family
 * ones additionally satisfy SqlFamilyFormApi (destination-form-api.ts) for live schema probing.
 */
export const DESTINATION_FORM_REGISTRY: DestinationFormRegistry<DestinationType> = {
  SqlServer: SqlServerDestinationFormComponent,
  AzureSql: AzureSqlDestinationFormComponent,
  MySql: MySqlDestinationFormComponent,
  PostgreSql: PostgreSqlDestinationFormComponent,
  Csv: CsvDestinationFormComponent,
  Mongo: MongoDestinationFormComponent,
  Sftp: SftpDestinationFormComponent,
  InMemory: InMemoryDestinationFormComponent,
  RestApi: RestApiDestinationFormComponent,
  BlobStorage: BlobStorageDestinationFormComponent,
  Excel: ExcelDestinationFormComponent,
  PowerBi: PowerBiDestinationFormComponent,
  Snowflake: SnowflakeDestinationFormComponent,
  FhirRepository: FhirRepositoryDestinationFormComponent,
  Medplum: MedplumDestinationFormComponent,
  AzureFhirService: AzureFhirServiceDestinationFormComponent,
  S3: S3DestinationFormComponent,
  Ndjson: NdjsonDestinationFormComponent,
  Parquet: ParquetDestinationFormComponent,
  Tableau: TableauDestinationFormComponent,
  Pdf: PdfDestinationFormComponent,
  Avro: AvroDestinationFormComponent,
  Protobuf: ProtobufDestinationFormComponent,
  Databricks: DatabricksDestinationFormComponent,
  DataLakeWebhook: DataLakeWebhookDestinationFormComponent,
  DataFabricAzure: DataFabricDestinationFormComponent,
  // Same form as the Files surface: one Fabric connection shape (workspace/item/Entra auth), with the
  // Warehouse-only fields it already carries. The landing mode is implied by the destination type.
  DataFabricWarehouse: DataFabricDestinationFormComponent,
};
