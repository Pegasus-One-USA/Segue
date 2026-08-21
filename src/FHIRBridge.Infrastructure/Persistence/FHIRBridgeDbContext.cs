using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Messaging;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class FHIRBridgeDbContext : DbContext
{
    public FHIRBridgeDbContext(DbContextOptions<FHIRBridgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<SourceConfiguration> SourceConfigurations => Set<SourceConfiguration>();
    public DbSet<WebhookConfiguration> WebhookConfigurations => Set<WebhookConfiguration>();
    public DbSet<DestinationConfiguration> DestinationConfigurations => Set<DestinationConfiguration>();
    public DbSet<MappingProfile> MappingProfiles => Set<MappingProfile>();
    public DbSet<SchemaMapping> SchemaMappings => Set<SchemaMapping>();
    public DbSet<TransformationRule> TransformationRules => Set<TransformationRule>();
    public DbSet<DeIdentificationProfile> DeIdentificationProfiles => Set<DeIdentificationProfile>();
    public DbSet<ResourcePipelineRoute> ResourcePipelineRoutes => Set<ResourcePipelineRoute>();
    public DbSet<SourceCapabilityProfile> SourceCapabilityProfiles => Set<SourceCapabilityProfile>();
    public DbSet<ConfiguredPipelineRunRecord> ConfiguredPipelineRuns => Set<ConfiguredPipelineRunRecord>();
    public DbSet<BulkExportJob> BulkExportJobs => Set<BulkExportJob>();
    public DbSet<PipelineRunRouteExecution> PipelineRunRouteExecutions => Set<PipelineRunRouteExecution>();
    public DbSet<PipelineRunResourceRecord> PipelineRunResourceRecords => Set<PipelineRunResourceRecord>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<PermissionCategory> PermissionCategories => Set<PermissionCategory>();
    public DbSet<PermissionGroup> PermissionGroups => Set<PermissionGroup>();
    public DbSet<PermissionAllocation> PermissionAllocations => Set<PermissionAllocation>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ProvisionedSecret> ProvisionedSecrets => Set<ProvisionedSecret>();
    public DbSet<EhrEndpoint> EhrEndpoints => Set<EhrEndpoint>();
    public DbSet<AllowedCorsOrigin> AllowedCorsOrigins => Set<AllowedCorsOrigin>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<UserFhirContextBinding> UserFhirContextBindings => Set<UserFhirContextBinding>();
    public DbSet<LoincConcept> LoincConcepts => Set<LoincConcept>();
    public DbSet<LoincPart> LoincParts => Set<LoincPart>();
    public DbSet<LoincGroup> LoincGroups => Set<LoincGroup>();
    public DbSet<LoincAnswerList> LoincAnswerLists => Set<LoincAnswerList>();
    public DbSet<LoincConceptMap> LoincConceptMaps => Set<LoincConceptMap>();
    public DbSet<LoincVersion> LoincVersions => Set<LoincVersion>();
    public DbSet<LoincImportHistory> LoincImportHistory => Set<LoincImportHistory>();
    public DbSet<SnomedConcept> SnomedConcepts => Set<SnomedConcept>();
    public DbSet<SnomedDescription> SnomedDescriptions => Set<SnomedDescription>();
    public DbSet<SnomedRelationship> SnomedRelationships => Set<SnomedRelationship>();
    public DbSet<SnomedVersion> SnomedVersions => Set<SnomedVersion>();
    public DbSet<SnomedImportHistory> SnomedImportHistory => Set<SnomedImportHistory>();
    public DbSet<Icd10Code> Icd10Codes => Set<Icd10Code>();
    public DbSet<Icd10Version> Icd10Versions => Set<Icd10Version>();
    public DbSet<Icd10ImportHistory> Icd10ImportHistory => Set<Icd10ImportHistory>();
    public DbSet<RxNormConcept> RxNormConcepts => Set<RxNormConcept>();
    public DbSet<RxNormVersion> RxNormVersions => Set<RxNormVersion>();
    public DbSet<RxNormImportHistory> RxNormImportHistory => Set<RxNormImportHistory>();
    public DbSet<NdcProduct> NdcProducts => Set<NdcProduct>();
    public DbSet<NdcVersion> NdcVersions => Set<NdcVersion>();
    public DbSet<NdcImportHistory> NdcImportHistory => Set<NdcImportHistory>();
    public DbSet<Icd10PcsCode> Icd10PcsCodes => Set<Icd10PcsCode>();
    public DbSet<Icd10PcsVersion> Icd10PcsVersions => Set<Icd10PcsVersion>();
    public DbSet<Icd10PcsImportHistory> Icd10PcsImportHistory => Set<Icd10PcsImportHistory>();
    public DbSet<HcpcsCode> HcpcsCodes => Set<HcpcsCode>();
    public DbSet<HcpcsVersion> HcpcsVersions => Set<HcpcsVersion>();
    public DbSet<HcpcsImportHistory> HcpcsImportHistory => Set<HcpcsImportHistory>();
    public DbSet<CvxCode> CvxCodes => Set<CvxCode>();
    public DbSet<CvxVersion> CvxVersions => Set<CvxVersion>();
    public DbSet<CvxImportHistory> CvxImportHistory => Set<CvxImportHistory>();
    public DbSet<UcumUnit> UcumUnits => Set<UcumUnit>();
    public DbSet<UcumVersion> UcumVersions => Set<UcumVersion>();
    public DbSet<UcumImportHistory> UcumImportHistory => Set<UcumImportHistory>();

    // Ranked-workflow graph engine (Scenario A): durable pipeline graphs + per-node run history.
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowNode> WorkflowNodes => Set<WorkflowNode>();
    public DbSet<WorkflowNodeConfiguration> WorkflowNodeConfigurations => Set<WorkflowNodeConfiguration>();
    public DbSet<WorkflowEdge> WorkflowEdges => Set<WorkflowEdge>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowNodeRun> WorkflowNodeRuns => Set<WorkflowNodeRun>();
    public DbSet<WorkflowNodeRunPayload> WorkflowNodeRunPayloads => Set<WorkflowNodeRunPayload>();
    public DbSet<FieldLineageEntry> FieldLineageEntries => Set<FieldLineageEntry>();

    // Governance: immutable audit/access/authentication trail + mutable security-event triage.
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WorkflowAuditLog> WorkflowAuditLogs => Set<WorkflowAuditLog>();
    public DbSet<DataAccessLog> DataAccessLogs => Set<DataAccessLog>();
    public DbSet<AuthenticationLog> AuthenticationLogs => Set<AuthenticationLog>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<AuthorizationLog> AuthorizationLogs => Set<AuthorizationLog>();
    public DbSet<ArchiveManifestEntry> ArchiveManifestEntries => Set<ArchiveManifestEntry>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertHistoryEntry> AlertHistoryEntries => Set<AlertHistoryEntry>();
    public DbSet<SchedulerHistory> SchedulerHistory => Set<SchedulerHistory>();
    public DbSet<RetryHistory> RetryHistory => Set<RetryHistory>();
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();
    public DbSet<ErrorResolution> ErrorResolutions => Set<ErrorResolution>();
    public DbSet<ApiRequestLog> ApiRequestLogs => Set<ApiRequestLog>();
    public DbSet<ExportHistory> ExportHistory => Set<ExportHistory>();
    public DbSet<NotificationHistory> NotificationHistory => Set<NotificationHistory>();
    public DbSet<ValidationFailureLog> ValidationFailureLogs => Set<ValidationFailureLog>();
    public DbSet<EndpointHealthCheck> EndpointHealthChecks => Set<EndpointHealthCheck>();
    public DbSet<SmartLaunchLog> SmartLaunchLogs => Set<SmartLaunchLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All DateTime values in this system represent UTC instants (DateTime.UtcNow at the
        // write site). Stamping Kind=Utc on every read/write ensures the API serializes them
        // with a "Z" suffix so the portal correctly converts to the viewer's local time.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FHIRBridgeDbContext).Assembly);

        // Cross-cutting conventions applied after the per-entity configurations:
        //  • soft-deletable entities get a global "hide deleted rows" query filter
        //  • every entity carrying a RowVersion gets it mapped as an optimistic-concurrency token
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                SoftDeleteFilterMethod
                    .MakeGenericMethod(clrType)
                    .Invoke(null, [modelBuilder]);
            }

            if (!entityType.IsOwned() && entityType.FindProperty(nameof(AuditableEntity<int>.RowVersion)) is not null)
            {
                modelBuilder.Entity(clrType)
                    .Property(nameof(AuditableEntity<int>.RowVersion))
                    .IsRowVersion();
            }

            // CreatedOnUtc/CreatedBy are always stamped by AuditingSaveChangesInterceptor before a row is ever
            // saved (see its Stamp() — every Added IAuditableEntity gets ApplyCreated(actor, now), and actor is
            // never null: CurrentUserInfo.AuditName falls back through UserId → Email → ExternalUserId →
            // "anonymous"). These DB-level NOT NULL + defaults are a safety net for any insert path that could
            // ever bypass the interceptor (raw SQL, a future direct-context seed), not the primary guarantee.
            // ModifiedOnUtc/ModifiedBy/DeletedOnUtc/DeletedBy stay nullable — legitimately absent until a row is
            // actually modified/deleted.
            if (!entityType.IsOwned() && typeof(IAuditableEntity).IsAssignableFrom(clrType))
            {
                modelBuilder.Entity(clrType)
                    .Property(nameof(IAuditableEntity.CreatedOnUtc))
                    .IsRequired()
                    .HasDefaultValueSql("GETUTCDATE()");

                modelBuilder.Entity(clrType)
                    .Property(nameof(IAuditableEntity.CreatedBy))
                    .IsRequired()
                    .HasMaxLength(320)
                    .HasDefaultValue("system");
            }
        }
    }

    private static readonly System.Reflection.MethodInfo SoftDeleteFilterMethod =
        typeof(FHIRBridgeDbContext).GetMethod(
            nameof(ApplySoftDeleteFilter),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static void ApplySoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ISoftDeletable
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => !e.IsDeleted);
    }

}
