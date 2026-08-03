namespace FHIRBridge.Domain.Enums;

public enum DestinationType
{
    InMemory = 0,
    SqlServer = 1,
    AzureSql = 2,
    RestApi = 3,
    BlobStorage = 4,
    Csv = 5,
    Excel = 6,
    PowerBi = 7,
    Snowflake = 8,
    FhirRepository = 9,
    PostgreSql = 10,
    MySql = 11,
    S3 = 12,
    Ndjson = 13,
    Parquet = 14,
    Sftp = 15,
    Tableau = 16,
    Pdf = 17,
    Avro = 18,
    Protobuf = 19,
    Databricks = 20,
    Mongo = 21
}
