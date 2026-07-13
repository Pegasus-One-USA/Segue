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
                    TriggerType = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguredPipelineRuns", x => x.Id);
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
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                name: "OperationalAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourcePipelineRouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ResourceCount = table.Column<int>(type: "int", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAuditLogs", x => x.Id);
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                name: "PipelineRunResourceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FetchedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NormalizedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedProfiles = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Warnings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataQualityScore = table.Column<double>(type: "float", nullable: true),
                    MasterPatientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NormalizedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MappedValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRunRouteExecutions", x => x.Id);
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
                name: "ResourceLineageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MappingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceLineageEntries", x => x.Id);
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    Scopes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ClientSecretKeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientSecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PrivateKeyKeyVaultName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PrivateKeySecretName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    KeyId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
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
                    RetrievalLastSuccessfulSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                name: "UserActivityAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Activity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OccurredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntryHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivityAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsLocalLoginEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    PasswordResetTokenHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PasswordResetTokenExpiresOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                name: "WorkflowDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TriggerScheduleExpression = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    TriggerBackfillOnFirstRun = table.Column<bool>(type: "bit", nullable: true),
                    LastTriggeredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
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
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodeRunPayloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                name: "MappingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationObject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Instances = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    SearchParameters = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    LastTriggeredOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                name: "WorkflowNodeConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSecret = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodeConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowNodeConfigurations_WorkflowNodes_WorkflowNodeId",
                        column: x => x.WorkflowNodeId,
                        principalTable: "WorkflowNodes",
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
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
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

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguredPipelineRuns_StartedOnUtc",
                table: "ConfiguredPipelineRuns",
                column: "StartedOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConfiguredPipelineRuns_Status",
                table: "ConfiguredPipelineRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EhrEndpoints_Name",
                table: "EhrEndpoints",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_EhrEndpoints_Vendor_VendorEndpointId",
                table: "EhrEndpoints",
                columns: new[] { "Vendor", "VendorEndpointId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MappingFields_MappingProfileId",
                table: "MappingFields",
                column: "MappingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_SourceConnectionId",
                table: "MappingProfiles",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAuditLogs_OccurredOnUtc",
                table: "OperationalAuditLogs",
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
                name: "IX_ProvisionedSecrets_KeyVaultName_SecretName",
                table: "ProvisionedSecrets",
                columns: new[] { "KeyVaultName", "SecretName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLineageEntries_OccurredOnUtc",
                table: "ResourceLineageEntries",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLineageEntries_PipelineRunId",
                table: "ResourceLineageEntries",
                column: "PipelineRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLineageEntries_SourceResourceId",
                table: "ResourceLineageEntries",
                column: "SourceResourceId");

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
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceCapabilityProfiles_SourceConnectionId",
                table: "SourceCapabilityProfiles",
                column: "SourceConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_OccurredOnUtc",
                table: "UserActivityAuditLogs",
                column: "OccurredOnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityAuditLogs_UserId",
                table: "UserActivityAuditLogs",
                column: "UserId");

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
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_MfaChallengeTokenHash",
                table: "Users",
                column: "MfaChallengeTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RefreshTokenHash",
                table: "Users",
                column: "RefreshTokenHash");

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
                name: "IX_WorkflowEdges_WorkflowDefinitionId",
                table: "WorkflowEdges",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodeConfigurations_WorkflowNodeId",
                table: "WorkflowNodeConfigurations",
                column: "WorkflowNodeId");

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
                name: "ConfiguredPipelineRuns");

            migrationBuilder.DropTable(
                name: "DestinationConfigurations");

            migrationBuilder.DropTable(
                name: "EhrEndpoints");

            migrationBuilder.DropTable(
                name: "MappingFields");

            migrationBuilder.DropTable(
                name: "OperationalAuditLogs");

            migrationBuilder.DropTable(
                name: "PermissionAllocations");

            migrationBuilder.DropTable(
                name: "PipelineRunResourceRecords");

            migrationBuilder.DropTable(
                name: "PipelineRunRouteExecutions");

            migrationBuilder.DropTable(
                name: "ProcessedMessages");

            migrationBuilder.DropTable(
                name: "ProvisionedSecrets");

            migrationBuilder.DropTable(
                name: "ResourceLineageEntries");

            migrationBuilder.DropTable(
                name: "ResourcePipelineRouteMappings");

            migrationBuilder.DropTable(
                name: "SourceCapabilityProfiles");

            migrationBuilder.DropTable(
                name: "UserActivityAuditLogs");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "WorkflowEdges");

            migrationBuilder.DropTable(
                name: "WorkflowNodeConfigurations");

            migrationBuilder.DropTable(
                name: "WorkflowNodeRunPayloads");

            migrationBuilder.DropTable(
                name: "WorkflowNodeRuns");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "ResourcePipelineRoutes");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WorkflowNodes");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "PermissionGroups");

            migrationBuilder.DropTable(
                name: "MappingProfiles");

            migrationBuilder.DropTable(
                name: "WebhookConfigurations");

            migrationBuilder.DropTable(
                name: "WorkflowDefinitions");

            migrationBuilder.DropTable(
                name: "PermissionCategories");

            migrationBuilder.DropTable(
                name: "SourceConnections");
        }
    }
}
