using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "terminology");

            migrationBuilder.CreateTable(
                name: "AlertHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FiredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Acknowledged = table.Column<bool>(type: "bit", nullable: false),
                    AcknowledgedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertHistoryEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventTypeFilter = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ThresholdCount = table.Column<int>(type: "int", nullable: false),
                    WindowMinutes = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllowedCorsOrigins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedCorsOrigins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiRequestLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "Outbound")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveManifestEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataClass = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ArchivedThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FileLocation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveManifestEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Module = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EntityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OldValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    NewValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthenticationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AuthenticationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthorizationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PermissionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BulkExportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExportRequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PollAttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextPollNotBeforeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    KickedOffOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriorNodeOutputsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContextJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestedResourceTypesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkExportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfiguredPipelineRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ResourceTypes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ExtractedResourceCount = table.Column<int>(type: "int", nullable: false),
                    MappedRecordCount = table.Column<int>(type: "int", nullable: false),
                    WrittenRecordCount = table.Column<int>(type: "int", nullable: false),
                    Errors = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TriggerType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguredPipelineRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CvxCodes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FullVaccineName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvxCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "CvxImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvxImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CvxVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvxVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataAccessLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PatientId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataAccessLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeIdentificationProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeIdentificationProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DestinationConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DestinationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    KeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConnectionMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DeIdentificationProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DestinationConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EhrEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Vendor = table.Column<int>(type: "int", nullable: false),
                    VendorEndpointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FhirBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FormatType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EndpointType = table.Column<int>(type: "int", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EhrEndpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EndpointHealthChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndpointName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EndpointType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointHealthChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    StackTrace = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Module = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorReferenceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    UserFriendlyMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExecutionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WorkflowId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EndpointId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequestId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SpanId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    DiagnosisAction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DiagnosisCause = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorResolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ErrorReferenceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResolvedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorResolutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExportHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FieldLineageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DestinationField = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NodeOrder = table.Column<int>(type: "int", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<double>(type: "float", nullable: true),
                    ExecutedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceSystemType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SourceConnectionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DestinationTypeName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DestinationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldLineageEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HapiTerminologyImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeSystem = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HapiTerminologyImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HcpcsCodes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LongDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HcpcsCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "HcpcsImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HcpcsImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HcpcsVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HcpcsVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10Codes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OrderNumber = table.Column<int>(type: "int", nullable: false),
                    IsBillable = table.Column<bool>(type: "bit", nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LongDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10Codes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Icd10ImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedCodeCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10ImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10PcsCodes",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LongDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10PcsCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Icd10PcsImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10PcsImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10PcsVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10PcsVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Icd10Versions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Icd10Versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincAnswerLists",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnswerListId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AnswerListName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LoincCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AnswerCode = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AnswerDisplay = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincAnswerLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincConceptMaps",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetSystem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TargetCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Equivalence = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Display = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincConceptMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincConcepts",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LongCommonName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Class = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Component = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Property = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimeAspect = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    System = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Scale = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Method = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincConcepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincGroups",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentGroupId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincParts",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PartTypeName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PartName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PartDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincParts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoincVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NdcImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdcImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NdcProducts",
                schema: "terminology",
                columns: table => new
                {
                    ProductNdc = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    GenericName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BrandName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DosageForm = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdcProducts", x => x.ProductNdc);
                });

            migrationBuilder.CreateTable(
                name: "NdcVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdcVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NotificationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    AttachmentNames = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    EnableSsl = table.Column<bool>(type: "bit", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PasswordKeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PasswordSecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRunEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StepType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRunResourceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedProfiles = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Warnings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataQualityScore = table.Column<double>(type: "float", nullable: true),
                    MasterPatientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NormalizedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MappedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StoredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WriteStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunResourceRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRunRouteExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystemType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExtractedCount = table.Column<int>(type: "int", nullable: false),
                    MappedCount = table.Column<int>(type: "int", nullable: false),
                    WrittenCount = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ErrorReferenceId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunRouteExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DestinationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RequestedResourceTypes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExtractedResourceCount = table.Column<int>(type: "int", nullable: false),
                    WrittenResourceCount = table.Column<int>(type: "int", nullable: false),
                    FailureMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorReferenceId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ProcessedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "ProvisionedSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProtectedValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedSecrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetryHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Context = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RetryNumber = table.Column<int>(type: "int", nullable: false),
                    DelayMilliseconds = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetryHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsFullAccess = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RxNormConcepts",
                schema: "terminology",
                columns: table => new
                {
                    Rxcui = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: false),
                    TermType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RxNormConcepts", x => x.Rxcui);
                });

            migrationBuilder.CreateTable(
                name: "RxNormImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RxNormImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RxNormVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RxNormVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchedulerHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchedulerId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RunTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RouteCount = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulerHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DestinationTable = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DestinationField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Resolved = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmartLaunchLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LaunchType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    GrantedScope = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PatientContextGranted = table.Column<bool>(type: "bit", nullable: true),
                    TokenCacheKeyHash = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartLaunchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedConcepts",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    EffectiveTime = table.Column<DateOnly>(type: "date", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    ModuleId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    DefinitionStatusId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Fsn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PreferredTerm = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedConcepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedDescriptions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    ConceptId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Term = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TypeId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    LanguageCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    CaseSignificanceId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedDescriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedRelationships",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    DestinationId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    TypeId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    RelationshipGroup = table.Column<int>(type: "int", nullable: false),
                    CharacteristicTypeId = table.Column<string>(type: "nvarchar(18)", maxLength: 18, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedRelationships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnomedVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnomedVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceCapabilityProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FhirVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConfiguredScopes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RawCapabilityJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DiscoveredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCapabilityProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystemType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AuthenticationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TokenEndpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClientSecretKeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientSecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PrivateKeyKeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PrivateKeySecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    KeyId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    JwksUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DiscoveredScopes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PracticeId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AuthPlacement = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AuthorizationEndpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ApplicationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RedirectUris = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LaunchUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TrustedIssuers = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PatientSelectionMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PostLaunchRedirectUri = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LaunchDisplayMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalResourceTypes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetrievalSearchCriteria = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetrievalIncrementalSyncEnabled = table.Column<bool>(type: "bit", nullable: true),
                    RetrievalPageSize = table.Column<int>(type: "int", nullable: true),
                    RetrievalSortOrder = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalIncludeParameters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetrievalRevIncludeParameters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetrievalRetryPolicy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalTimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    RetrievalMaxRecordsPerRun = table.Column<int>(type: "int", nullable: true),
                    RetrievalExportScope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalGroupId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RetrievalPatientIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetrievalOutputFormat = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RetrievalLastSuccessfulSyncByResourceType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransformationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DestinationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DestinationField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourcePipelineRouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceField = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NodeType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    OnNull = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorPolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OnNullDefaultValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ArrayMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FhirWriteBackJsonPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExecutionPhase = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "PostMapping"),
                    DeIdentificationProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ExpectedValueType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransformationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CODESYSTEM",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeSystemUri = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CsName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CurrentVersionPid = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CODESYSTEM", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CODESYSTEM_VER",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeSystemPid = table.Column<long>(type: "bigint", nullable: false),
                    CsVersionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CsDisplay = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CODESYSTEM_VER", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "TRM_CONCEPT",
                schema: "terminology",
                columns: table => new
                {
                    Pid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CodeSystemPid = table.Column<long>(type: "bigint", nullable: false),
                    CodeVal = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "SQL_Latin1_General_CP1_CS_AS"),
                    Display = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TRM_CONCEPT", x => x.Pid);
                });

            migrationBuilder.CreateTable(
                name: "UcumImportHistory",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedConceptCount = table.Column<int>(type: "int", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UcumImportHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UcumUnits",
                schema: "terminology",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PrintSymbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UcumUnits", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "UcumVersions",
                schema: "terminology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleaseDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ImportedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UcumVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MonotonicTicks = table.Column<long>(type: "bigint", nullable: false),
                    UserCount = table.Column<int>(type: "int", nullable: false),
                    SourceConnectionCount = table.Column<int>(type: "int", nullable: false),
                    TenantCount = table.Column<int>(type: "int", nullable: false),
                    WorkflowCount = table.Column<int>(type: "int", nullable: false),
                    CumulativeConfiguredPipelineRunCount = table.Column<long>(type: "bigint", nullable: false),
                    CumulativeRuntimeWorkflowRunCount = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedRecordsThisMonth = table.Column<long>(type: "bigint", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserFhirContextBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserIdentity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CallerIdentity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CallerFhirUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFhirContextBindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValidationFailureLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WarningsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    DataQualityScore = table.Column<double>(type: "float", nullable: true),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationFailureLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NodeType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourceIdHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    InputContract = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OutputContract = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OccurredOnUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsPubliclyLaunchable = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TriggerScheduleExpression = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    TriggerBackfillOnFirstRun = table.Column<bool>(type: "bit", nullable: true),
                    TriggerTimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, defaultValue: "UTC"),
                    LastTriggeredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    UpdatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    WorkflowNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowNodeRunPayloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowNodeRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Contract = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: true),
                    ResourceTypeCountsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeliveryDetailJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodeRunPayloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowNumberSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNumberSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionVersion = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorReferenceId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionGroups_PermissionCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "PermissionCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRunSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourceCount = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineRunSteps_PipelineRuns_PipelineRunId",
                        column: x => x.PipelineRunId,
                        principalTable: "PipelineRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetrievalMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalResourceTypes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetrievalSearchCriteria = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetrievalIncrementalSyncEnabled = table.Column<bool>(type: "bit", nullable: true),
                    RetrievalPageSize = table.Column<int>(type: "int", nullable: true),
                    RetrievalSortOrder = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalIncludeParameters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetrievalRevIncludeParameters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetrievalRetryPolicy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalTimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    RetrievalMaxRecordsPerRun = table.Column<int>(type: "int", nullable: true),
                    RetrievalExportScope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalGroupId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RetrievalPatientIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetrievalOutputFormat = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RetrievalLastSuccessfulSyncByResourceType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceConfigurations_SourceConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "SourceConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BrandConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PrimaryColor = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    SecondaryColor = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    AccentColor = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    BackgroundColor = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    FontFamily = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SupportEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SupportPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Website = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EmailFooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DefaultThemeMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LoaderStyle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DarkLogoUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FaviconUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LoginBackgroundUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LoginIllustrationUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmailLogoUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrandConfigurations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsLocalLoginEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    PasswordResetTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PasswordResetTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MagicLinkTokenHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MagicLinkTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastLoginOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPasswordChangedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PasswordExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MfaEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MfaSecret = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MfaBackupCodeHashes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    MfaEnrolledOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MfaChallengeTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MfaChallengeExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MustSetupMfa = table.Column<bool>(type: "bit", nullable: false),
                    InvitationTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InvitationTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefreshTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefreshTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefreshTokenRememberMe = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowEdges_WorkflowDefinitions_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "WorkflowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    SubRank = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PositionX = table.Column<double>(type: "float", nullable: false),
                    PositionY = table.Column<double>(type: "float", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CheckpointUrlEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowNodes_WorkflowDefinitions_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "WorkflowDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowNodeRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    SubRank = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LineageJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodeRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowNodeRuns_WorkflowRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Instances = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Permissions_PermissionGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "PermissionGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MappingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationObject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MappingJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MappingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MappingProfiles_SourceConfigurations_SourceConfigurationId",
                        column: x => x.SourceConfigurationId,
                        principalTable: "SourceConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MappingProfiles_SourceConnections_SourceConnectionId",
                        column: x => x.SourceConnectionId,
                        principalTable: "SourceConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermissionAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PermissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionAllocations", x => x.Id);
                    table.CheckConstraint("CK_PermissionAllocations_RoleXorUser", "([RoleId] IS NOT NULL AND [UserId] IS NULL) OR ([RoleId] IS NULL AND [UserId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PermissionAllocations_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionAllocations_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionAllocations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MappingFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    JsonPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ValueType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DefaultValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Format = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DestinationObject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NormalizationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TerminologySystemJsonPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TerminologyCodeJsonPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ArrayPolicy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Scalar"),
                    Cardinality = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ArrayAncestors = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsUpsertKey = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CorrelationCodeJsonPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationCodeValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ParentTable = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ParentKeyColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ForeignKeyColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferenceLookupTable = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ReferenceLookupKeyColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MappingFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MappingFields_MappingProfiles_MappingProfileId",
                        column: x => x.MappingProfileId,
                        principalTable: "MappingProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourcePipelineRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WebhookConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngestionMode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ScheduleExpression = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "UTC"),
                    SearchParameters = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    LastTriggeredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false, defaultValue: "system"),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePipelineRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourcePipelineRoutes_MappingProfiles_MappingProfileId",
                        column: x => x.MappingProfileId,
                        principalTable: "MappingProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourcePipelineRoutes_WebhookConfigurations_WebhookConfigurationId",
                        column: x => x.WebhookConfigurationId,
                        principalTable: "WebhookConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResourcePipelineRouteMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourcePipelineRouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ExecutionOrder = table.Column<int>(type: "int", nullable: false),
                    SearchParameters = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePipelineRouteMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourcePipelineRouteMappings_MappingProfiles_MappingProfileId",
                        column: x => x.MappingProfileId,
                        principalTable: "MappingProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourcePipelineRouteMappings_ResourcePipelineRoutes_ResourcePipelineRouteId",
                        column: x => x.ResourcePipelineRouteId,
                        principalTable: "ResourcePipelineRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourcePipelineRouteMappingParentReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourcePipelineRouteMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentMappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceFieldOverride = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePipelineRouteMappingParentReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourcePipelineRouteMappingParentReferences_ResourcePipelineRouteMappings_ResourcePipelineRouteMappingId",
                        column: x => x.ResourcePipelineRouteMappingId,
                        principalTable: "ResourcePipelineRouteMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistoryEntries_AlertRuleId",
                table: "AlertHistoryEntries",
                column: "AlertRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistoryEntries_FiredOnUtc",
                table: "AlertHistoryEntries",
                column: "FiredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AllowedCorsOrigins_OriginUrl",
                table: "AllowedCorsOrigins",
                column: "OriginUrl",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CorrelationId",
                table: "ApiRequestLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CorrelationId_Direction",
                table: "ApiRequestLogs",
                columns: new[] { "CorrelationId", "Direction" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_OccurredOnUtc",
                table: "ApiRequestLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_StatusCode",
                table: "ApiRequestLogs",
                column: "StatusCode");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveManifestEntries_CreatedOnUtc",
                table: "ArchiveManifestEntries",
                column: "CreatedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveManifestEntries_DataClass",
                table: "ArchiveManifestEntries",
                column: "DataClass");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CorrelationId",
                table: "AuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredOnUtc",
                table: "AuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_SequenceNumber",
                table: "AuditLogs",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationLogs_CorrelationId",
                table: "AuthenticationLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationLogs_OccurredOnUtc",
                table: "AuthenticationLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationLogs_UserEmail",
                table: "AuthenticationLogs",
                column: "UserEmail");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationLogs_CorrelationId",
                table: "AuthorizationLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationLogs_OccurredOnUtc",
                table: "AuthorizationLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationLogs_UserEmail",
                table: "AuthorizationLogs",
                column: "UserEmail");

            migrationBuilder.CreateIndex(
                name: "IX_BrandConfigurations_TenantId",
                table: "BrandConfigurations",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkExportJobs_CorrelationId",
                table: "BulkExportJobs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkExportJobs_Status_NextPollNotBeforeUtc",
                table: "BulkExportJobs",
                columns: new[] { "Status", "NextPollNotBeforeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkExportJobs_WorkflowRunId",
                table: "BulkExportJobs",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguredPipelineRuns_CorrelationId",
                table: "ConfiguredPipelineRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguredPipelineRuns_StartedOnUtc",
                table: "ConfiguredPipelineRuns",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguredPipelineRuns_Status",
                table: "ConfiguredPipelineRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CvxCodes_IsActive_Code",
                schema: "terminology",
                table: "CvxCodes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_CvxImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "CvxImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CvxVersions_IsActive",
                schema: "terminology",
                table: "CvxVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CvxVersions_Version",
                schema: "terminology",
                table: "CvxVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_CorrelationId",
                table: "DataAccessLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_OccurredOnUtc",
                table: "DataAccessLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_PatientId",
                table: "DataAccessLogs",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_DataAccessLogs_PipelineRunId",
                table: "DataAccessLogs",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_DeIdentificationProfiles_Name",
                table: "DeIdentificationProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EhrEndpoints_Name",
                table: "EhrEndpoints",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_EhrEndpoints_Vendor_VendorEndpointId",
                table: "EhrEndpoints",
                columns: new[] { "Vendor", "VendorEndpointId" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointHealthChecks_EndpointName",
                table: "EndpointHealthChecks",
                column: "EndpointName");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointHealthChecks_OccurredOnUtc",
                table: "EndpointHealthChecks",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EndpointHealthChecks_Status",
                table: "EndpointHealthChecks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_Category",
                table: "ErrorLogs",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_CorrelationId",
                table: "ErrorLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_ErrorReferenceId",
                table: "ErrorLogs",
                column: "ErrorReferenceId",
                unique: true,
                filter: "[ErrorReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_ExecutionId",
                table: "ErrorLogs",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_OccurredOnUtc",
                table: "ErrorLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_Severity",
                table: "ErrorLogs",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorResolutions_ErrorReferenceId",
                table: "ErrorResolutions",
                column: "ErrorReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExportHistory_CorrelationId",
                table: "ExportHistory",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ExportHistory_OccurredOnUtc",
                table: "ExportHistory",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExportHistory_PipelineRunId",
                table: "ExportHistory",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_RecordedAtUtc",
                table: "FieldLineageEntries",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_WorkflowRunId",
                table: "FieldLineageEntries",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldLineageEntries_WorkflowRunId_ResourceType_ResourceId",
                table: "FieldLineageEntries",
                columns: new[] { "WorkflowRunId", "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_HapiTerminologyImportHistory_CodeSystem_StartedOnUtc",
                schema: "terminology",
                table: "HapiTerminologyImportHistory",
                columns: new[] { "CodeSystem", "StartedOnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsCodes_IsActive_Code",
                schema: "terminology",
                table: "HcpcsCodes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "HcpcsImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsVersions_IsActive",
                schema: "terminology",
                table: "HcpcsVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HcpcsVersions_Version",
                schema: "terminology",
                table: "HcpcsVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Icd10Codes_IsActive_Code",
                schema: "terminology",
                table: "Icd10Codes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Icd10ImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "Icd10ImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsCodes_IsActive_Code",
                schema: "terminology",
                table: "Icd10PcsCodes",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "Icd10PcsImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsVersions_IsActive",
                schema: "terminology",
                table: "Icd10PcsVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10PcsVersions_Version",
                schema: "terminology",
                table: "Icd10PcsVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Icd10Versions_IsActive",
                schema: "terminology",
                table: "Icd10Versions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Icd10Versions_Version",
                schema: "terminology",
                table: "Icd10Versions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincAnswerLists_AnswerListId_AnswerCode_Version",
                schema: "terminology",
                table: "LoincAnswerLists",
                columns: new[] { "AnswerListId", "AnswerCode", "Version" },
                unique: true,
                filter: "[AnswerCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoincConceptMaps_SourceSystem_SourceCode_TargetSystem_Version",
                schema: "terminology",
                table: "LoincConceptMaps",
                columns: new[] { "SourceSystem", "SourceCode", "TargetSystem", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_LoincConcepts_Code",
                schema: "terminology",
                table: "LoincConcepts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincConcepts_IsActive_Code",
                schema: "terminology",
                table: "LoincConcepts",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_LoincGroups_GroupId_Version",
                schema: "terminology",
                table: "LoincGroups",
                columns: new[] { "GroupId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "LoincImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LoincParts_PartNumber_Version",
                schema: "terminology",
                table: "LoincParts",
                columns: new[] { "PartNumber", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoincVersions_IsActive",
                schema: "terminology",
                table: "LoincVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LoincVersions_Version",
                schema: "terminology",
                table: "LoincVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MappingFields_MappingProfileId",
                table: "MappingFields",
                column: "MappingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_DestinationId",
                table: "MappingProfiles",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_SourceConfigurationId",
                table: "MappingProfiles",
                column: "SourceConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_SourceConnectionId",
                table: "MappingProfiles",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_NdcImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "NdcImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NdcProducts_IsActive_ProductNdc",
                schema: "terminology",
                table: "NdcProducts",
                columns: new[] { "IsActive", "ProductNdc" });

            migrationBuilder.CreateIndex(
                name: "IX_NdcVersions_IsActive",
                schema: "terminology",
                table: "NdcVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_NdcVersions_Version",
                schema: "terminology",
                table: "NdcVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationHistory_CorrelationId",
                table: "NotificationHistory",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationHistory_OccurredOnUtc",
                table: "NotificationHistory",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAllocations_PermissionId",
                table: "PermissionAllocations",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAllocations_RoleId_PermissionId",
                table: "PermissionAllocations",
                columns: new[] { "RoleId", "PermissionId" },
                unique: true,
                filter: "[RoleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionAllocations_UserId_PermissionId",
                table: "PermissionAllocations",
                columns: new[] { "UserId", "PermissionId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionCategories_Name",
                table: "PermissionCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGroups_CategoryId",
                table: "PermissionGroups",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGroups_Name",
                table: "PermissionGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_GroupId",
                table: "Permissions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Name",
                table: "Permissions",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunEvents_OccurredOnUtc",
                table: "PipelineRunEvents",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunEvents_PipelineRunId",
                table: "PipelineRunEvents",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunResourceRecords_FetchedAtUtc",
                table: "PipelineRunResourceRecords",
                column: "FetchedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunResourceRecords_RouteExecutionId",
                table: "PipelineRunResourceRecords",
                column: "RouteExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunRouteExecutions_PipelineRunId",
                table: "PipelineRunRouteExecutions",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunRouteExecutions_StartedOnUtc",
                table: "PipelineRunRouteExecutions",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunRouteExecutions_Status",
                table: "PipelineRunRouteExecutions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_CorrelationId",
                table: "PipelineRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_StartedOnUtc",
                table: "PipelineRuns",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_Status",
                table: "PipelineRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRunSteps_PipelineRunId",
                table: "PipelineRunSteps",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedSecrets_KeyVaultName_SecretName",
                table: "ProvisionedSecrets",
                columns: new[] { "KeyVaultName", "SecretName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePipelineRouteMappingParentReferences_ResourcePipelineRouteMappingId_ParentMappingProfileId",
                table: "ResourcePipelineRouteMappingParentReferences",
                columns: new[] { "ResourcePipelineRouteMappingId", "ParentMappingProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePipelineRouteMappings_MappingProfileId",
                table: "ResourcePipelineRouteMappings",
                column: "MappingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePipelineRouteMappings_ResourcePipelineRouteId_MappingProfileId",
                table: "ResourcePipelineRouteMappings",
                columns: new[] { "ResourcePipelineRouteId", "MappingProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePipelineRoutes_MappingProfileId",
                table: "ResourcePipelineRoutes",
                column: "MappingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePipelineRoutes_WebhookConfigurationId_MappingProfileId",
                table: "ResourcePipelineRoutes",
                columns: new[] { "WebhookConfigurationId", "MappingProfileId" },
                unique: true,
                filter: "[WebhookConfigurationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RetryHistory_CorrelationId",
                table: "RetryHistory",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_RetryHistory_OccurredOnUtc",
                table: "RetryHistory",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_RxNormConcepts_IsActive_Rxcui",
                schema: "terminology",
                table: "RxNormConcepts",
                columns: new[] { "IsActive", "Rxcui" });

            migrationBuilder.CreateIndex(
                name: "IX_RxNormImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "RxNormImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RxNormVersions_IsActive",
                schema: "terminology",
                table: "RxNormVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RxNormVersions_Version",
                schema: "terminology",
                table: "RxNormVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerHistory_CorrelationId",
                table: "SchedulerHistory",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerHistory_RunTimeUtc",
                table: "SchedulerHistory",
                column: "RunTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaMappings_SourceSystem_ResourceType_DestinationTable_DestinationField",
                table: "SchemaMappings",
                columns: new[] { "SourceSystem", "ResourceType", "DestinationTable", "DestinationField" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_OccurredOnUtc",
                table: "SecurityEvents",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Resolved",
                table: "SecurityEvents",
                column: "Resolved");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Severity",
                table: "SecurityEvents",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_SmartLaunchLogs_CorrelationId",
                table: "SmartLaunchLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartLaunchLogs_OccurredOnUtc",
                table: "SmartLaunchLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SmartLaunchLogs_SourceConnectionId",
                table: "SmartLaunchLogs",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedConcepts_Active_Id",
                schema: "terminology",
                table: "SnomedConcepts",
                columns: new[] { "Active", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SnomedDescriptions_ConceptId",
                schema: "terminology",
                table: "SnomedDescriptions",
                column: "ConceptId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "SnomedImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedRelationships_DestinationId",
                schema: "terminology",
                table: "SnomedRelationships",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedRelationships_SourceId",
                schema: "terminology",
                table: "SnomedRelationships",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedVersions_IsActive",
                schema: "terminology",
                table: "SnomedVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_SnomedVersions_Version",
                schema: "terminology",
                table: "SnomedVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceCapabilityProfiles_SourceConnectionId",
                table: "SourceCapabilityProfiles",
                column: "SourceConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceConfigurations_ConnectionId",
                table: "SourceConfigurations",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Key",
                table: "SystemSettings",
                column: "Key",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Code",
                table: "Tenants",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_ExecutionPhase_DeIdentificationProfileId_ResourceType",
                table: "TransformationRules",
                columns: new[] { "ExecutionPhase", "DeIdentificationProfileId", "ResourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_ExecutionPhase_Scope_ResourceType_SourceField",
                table: "TransformationRules",
                columns: new[] { "ExecutionPhase", "Scope", "ResourceType", "SourceField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_DestinationType_DestinationField",
                table: "TransformationRules",
                columns: new[] { "Scope", "DestinationType", "DestinationField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourcePipelineRouteId_ResourceType_DestinationField_SourceSystem_SourceField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourcePipelineRouteId", "ResourceType", "DestinationField", "SourceSystem", "SourceField" });

            migrationBuilder.CreateIndex(
                name: "IX_TransformationRules_Scope_ResourceType_DestinationField_SourceSystem_SourceField",
                table: "TransformationRules",
                columns: new[] { "Scope", "ResourceType", "DestinationField", "SourceSystem", "SourceField" });

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CODESYSTEM_CodeSystemUri",
                schema: "terminology",
                table: "TRM_CODESYSTEM",
                column: "CodeSystemUri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CODESYSTEM_VER_CodeSystemPid_CsVersionId",
                schema: "terminology",
                table: "TRM_CODESYSTEM_VER",
                columns: new[] { "CodeSystemPid", "CsVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CONCEPT_CodeSystemPid_CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                columns: new[] { "CodeSystemPid", "CodeVal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TRM_CONCEPT_CodeVal",
                schema: "terminology",
                table: "TRM_CONCEPT",
                column: "CodeVal");

            migrationBuilder.CreateIndex(
                name: "IX_UcumImportHistory_StartedOnUtc",
                schema: "terminology",
                table: "UcumImportHistory",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UcumUnits_IsActive_Code",
                schema: "terminology",
                table: "UcumUnits",
                columns: new[] { "IsActive", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_UcumVersions_IsActive",
                schema: "terminology",
                table: "UcumVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_UcumVersions_Version",
                schema: "terminology",
                table: "UcumVersions",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageLedgerEntries_ObservedUtc",
                table: "UsageLedgerEntries",
                column: "ObservedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UsageLedgerEntries_SequenceNumber",
                table: "UsageLedgerEntries",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserFhirContextBindings_SourceConnectionId_UserIdentity",
                table: "UserFhirContextBindings",
                columns: new[] { "SourceConnectionId", "UserIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalUserId",
                table: "Users",
                column: "ExternalUserId",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Users_MfaChallengeTokenHash",
                table: "Users",
                column: "MfaChallengeTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RefreshTokenHash",
                table: "Users",
                column: "RefreshTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationFailureLogs_CorrelationId",
                table: "ValidationFailureLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationFailureLogs_OccurredOnUtc",
                table: "ValidationFailureLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationFailureLogs_ResourceType",
                table: "ValidationFailureLogs",
                column: "ResourceType");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookConfigurations_Path",
                table: "WebhookConfigurations",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookConfigurations_SourceConnectionId_ResourceType",
                table: "WebhookConfigurations",
                columns: new[] { "SourceConnectionId", "ResourceType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAuditLogs_OccurredOnUtc",
                table: "WorkflowAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAuditLogs_WorkflowRunId",
                table: "WorkflowAuditLogs",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_WorkflowNumber",
                table: "WorkflowDefinitions",
                column: "WorkflowNumber",
                unique: true,
                filter: "[WorkflowNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowEdges_WorkflowDefinitionId",
                table: "WorkflowEdges",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeRunPayloads_RecordedAtUtc",
                table: "WorkflowNodeRunPayloads",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeRunPayloads_WorkflowRunId",
                table: "WorkflowNodeRunPayloads",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeRuns_WorkflowRunId",
                table: "WorkflowNodeRuns",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodes_WorkflowDefinitionId",
                table: "WorkflowNodes",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNumberSequences_PeriodKey",
                table: "WorkflowNumberSequences",
                column: "PeriodKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_CorrelationId",
                table: "WorkflowRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_StartedAt",
                table: "WorkflowRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkflowDefinitionId",
                table: "WorkflowRuns",
                column: "WorkflowDefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertHistoryEntries");

            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "AllowedCorsOrigins");

            migrationBuilder.DropTable(
                name: "ApiRequestLogs");

            migrationBuilder.DropTable(
                name: "ArchiveManifestEntries");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "AuthenticationLogs");

            migrationBuilder.DropTable(
                name: "AuthorizationLogs");

            migrationBuilder.DropTable(
                name: "BrandConfigurations");

            migrationBuilder.DropTable(
                name: "BulkExportJobs");

            migrationBuilder.DropTable(
                name: "ConfiguredPipelineRuns");

            migrationBuilder.DropTable(
                name: "CvxCodes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "CvxImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "CvxVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "DataAccessLogs");

            migrationBuilder.DropTable(
                name: "DeIdentificationProfiles");

            migrationBuilder.DropTable(
                name: "DestinationConfigurations");

            migrationBuilder.DropTable(
                name: "EhrEndpoints");

            migrationBuilder.DropTable(
                name: "EndpointHealthChecks");

            migrationBuilder.DropTable(
                name: "ErrorLogs");

            migrationBuilder.DropTable(
                name: "ErrorResolutions");

            migrationBuilder.DropTable(
                name: "ExportHistory");

            migrationBuilder.DropTable(
                name: "FieldLineageEntries");

            migrationBuilder.DropTable(
                name: "HapiTerminologyImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "HcpcsCodes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "HcpcsImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "HcpcsVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10Codes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10ImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10PcsCodes",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10PcsImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10PcsVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "Icd10Versions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincAnswerLists",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincConceptMaps",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincConcepts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincGroups",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincParts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "LoincVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "MappingFields");

            migrationBuilder.DropTable(
                name: "NdcImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "NdcProducts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "NdcVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "NotificationHistory");

            migrationBuilder.DropTable(
                name: "NotificationSettings");

            migrationBuilder.DropTable(
                name: "PermissionAllocations");

            migrationBuilder.DropTable(
                name: "PipelineRunEvents");

            migrationBuilder.DropTable(
                name: "PipelineRunResourceRecords");

            migrationBuilder.DropTable(
                name: "PipelineRunRouteExecutions");

            migrationBuilder.DropTable(
                name: "PipelineRunSteps");

            migrationBuilder.DropTable(
                name: "ProcessedMessages");

            migrationBuilder.DropTable(
                name: "ProvisionedSecrets");

            migrationBuilder.DropTable(
                name: "ResourcePipelineRouteMappingParentReferences");

            migrationBuilder.DropTable(
                name: "RetryHistory");

            migrationBuilder.DropTable(
                name: "RxNormConcepts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "RxNormImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "RxNormVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SchedulerHistory");

            migrationBuilder.DropTable(
                name: "SchemaMappings");

            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "SmartLaunchLogs");

            migrationBuilder.DropTable(
                name: "SnomedConcepts",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedDescriptions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedRelationships",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SnomedVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "SourceCapabilityProfiles");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "TransformationRules");

            migrationBuilder.DropTable(
                name: "TRM_CODESYSTEM",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CODESYSTEM_VER",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "TRM_CONCEPT",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UcumImportHistory",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UcumUnits",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UcumVersions",
                schema: "terminology");

            migrationBuilder.DropTable(
                name: "UsageLedgerEntries");

            migrationBuilder.DropTable(
                name: "UserFhirContextBindings");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "ValidationFailureLogs");

            migrationBuilder.DropTable(
                name: "WorkflowAuditLogs");

            migrationBuilder.DropTable(
                name: "WorkflowEdges");

            migrationBuilder.DropTable(
                name: "WorkflowNodeRunPayloads");

            migrationBuilder.DropTable(
                name: "WorkflowNodeRuns");

            migrationBuilder.DropTable(
                name: "WorkflowNodes");

            migrationBuilder.DropTable(
                name: "WorkflowNumberSequences");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "PipelineRuns");

            migrationBuilder.DropTable(
                name: "ResourcePipelineRouteMappings");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "WorkflowDefinitions");

            migrationBuilder.DropTable(
                name: "PermissionGroups");

            migrationBuilder.DropTable(
                name: "ResourcePipelineRoutes");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "PermissionCategories");

            migrationBuilder.DropTable(
                name: "MappingProfiles");

            migrationBuilder.DropTable(
                name: "WebhookConfigurations");

            migrationBuilder.DropTable(
                name: "SourceConfigurations");

            migrationBuilder.DropTable(
                name: "SourceConnections");
        }
    }
}
