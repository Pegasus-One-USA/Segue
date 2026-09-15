using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Domain.Entities.Licensing;
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

    /// <summary>
    /// Set by <see cref="Workflows.SqlWorkflowDefinitionStore"/> immediately before the create-path
    /// <c>SaveChangesAsync</c> call it always issues for a genuinely NEW <see cref="WorkflowDefinition"/> (that
    /// store's save is always a delete-then-re-add across two separate <c>SaveChangesAsync</c> calls — see its
    /// own remarks — so a same-id EDIT reaches <c>SaveChangesAsync</c> looking identical to a create: one
    /// Added <see cref="WorkflowDefinition"/> entry, no corresponding Deleted entry in the same batch, because
    /// the old row was already removed by the prior save). <see cref="LicenseEnforcementSaveChangesInterceptor"/>
    /// reads this flag to tell the two apart without needing its own opinion about that store's implementation,
    /// and resets it to <c>false</c> immediately after use so it never leaks into an unrelated later save on
    /// the same scoped context instance.
    /// </summary>
    internal bool NextWorkflowDefinitionAddIsGenuineCreate { get; set; }

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
    public DbSet<UsageLedgerEntry> UsageLedgerEntries => Set<UsageLedgerEntry>();
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
    public DbSet<BrandConfiguration> BrandConfigurations => Set<BrandConfiguration>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    /// <summary>Per-period counters behind generated workflow numbers (WLW-ddMMyy-NNNN).</summary>
    public DbSet<WorkflowNumberSequence> WorkflowNumberSequences => Set<WorkflowNumberSequence>();
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
    public DbSet<TrmCodeSystem> TrmCodeSystems => Set<TrmCodeSystem>();
    public DbSet<TrmCodeSystemVer> TrmCodeSystemVers => Set<TrmCodeSystemVer>();
    public DbSet<TrmConcept> TrmConcepts => Set<TrmConcept>();
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
    public DbSet<HapiTerminologyImportHistory> HapiTerminologyImportHistory => Set<HapiTerminologyImportHistory>();

    // Ranked-workflow graph engine (Scenario A): durable pipeline graphs + per-node run history.
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowNode> WorkflowNodes => Set<WorkflowNode>();
    public DbSet<WorkflowEdge> WorkflowEdges => Set<WorkflowEdge>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowNodeRun> WorkflowNodeRuns => Set<WorkflowNodeRun>();
    public DbSet<WorkflowNodeRunPayload> WorkflowNodeRunPayloads => Set<WorkflowNodeRunPayload>();
    public DbSet<FieldLineageEntry> FieldLineageEntries => Set<FieldLineageEntry>();

    // Runtime DAG plane (Scenario B / bulk-export): pipeline run history — see SqlPipelineRunStore.
    public DbSet<FHIRBridge.Runtime.Domain.Entities.PipelineRun> PipelineRuns => Set<FHIRBridge.Runtime.Domain.Entities.PipelineRun>();
    public DbSet<FHIRBridge.Runtime.Domain.Entities.PipelineRunStep> PipelineRunSteps => Set<FHIRBridge.Runtime.Domain.Entities.PipelineRunStep>();
    public DbSet<FHIRBridge.Runtime.Domain.Entities.PipelineRunEvent> PipelineRunEvents => Set<FHIRBridge.Runtime.Domain.Entities.PipelineRunEvent>();

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

        // GETUTCDATE() is T-SQL only; Npgsql has no such function — timezone('utc', now()) is its equivalent.
        // Database.IsNpgsql() is safe to call even when only the SqlServer provider is active at runtime.
        var isNpgsql = Database.IsNpgsql();
        var createdOnUtcDefaultSql = isNpgsql ? "timezone('utc', now())" : "GETUTCDATE()";

        // SQL Server's collation name has no Postgres equivalent; Postgres's default "C"-locale
        // collation is already case-sensitive byte comparison, so no explicit collation is needed there.
        // See TrmConceptConfiguration for why this column must be case-sensitive at all.
        if (!isNpgsql)
        {
            modelBuilder.Entity<TrmConcept>()
                .Property(x => x.CodeVal)
                .UseCollation("SQL_Latin1_General_CP1_CS_AS");
        }

        // The unique index on WorkflowDefinitions.WorkflowNumber is filtered so that the many rows with a
        // null number (numbering disabled, or legacy rows) do not collide. The filter is raw SQL, so the
        // identifier quoting differs per provider — see WorkflowDefinitionEntityTypeConfiguration.
        modelBuilder.Entity<WorkflowDefinition>()
            .HasIndex(x => x.WorkflowNumber)
            .IsUnique()
            .HasFilter(isNpgsql ? "\"WorkflowNumber\" IS NOT NULL" : "[WorkflowNumber] IS NOT NULL");

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
                if (isNpgsql)
                {
                    // SQL Server's native "rowversion" type is DB-generated and auto-incrementing, so
                    // .IsRowVersion() (== ValueGeneratedOnAddOrUpdate + IsConcurrencyToken) is enough there —
                    // EF treats it as store-generated and never sends a value for it. Postgres has no
                    // equivalent column type for a plain byte[]: nothing populates it, so treating it the same
                    // way just means EF omits it from every INSERT and the NOT NULL constraint fails outright.
                    // (The usual Npgsql substitute, mapping the "xmin" system column, doesn't work here either —
                    // xmin can't be declared as a column in CREATE TABLE, Postgres reserves that name.)
                    // Instead: mark it concurrency-token-only (no store generation) and let
                    // AuditingSaveChangesInterceptor.Stamp stamp a fresh value on every Added/Modified entry —
                    // the same trick EF Core itself relies on for providers with no native row-versioning.
                    modelBuilder.Entity(clrType)
                        .Property(nameof(AuditableEntity<int>.RowVersion))
                        .IsConcurrencyToken();
                }
                else
                {
                    modelBuilder.Entity(clrType)
                        .Property(nameof(AuditableEntity<int>.RowVersion))
                        .IsRowVersion();
                }
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
                    .HasDefaultValueSql(createdOnUtcDefaultSql);

                modelBuilder.Entity(clrType)
                    .Property(nameof(IAuditableEntity.CreatedBy))
                    .IsRequired()
                    .HasMaxLength(320)
                    .HasDefaultValue("system");
            }
        }

        // WorkflowDefinition doesn't implement IAuditableEntity — it's a Runtime-domain aggregate configured
        // directly in WorkflowPersistenceConfigurations.cs, which sets its own CreatedOnUtc default — so the
        // loop above never reaches it. Overriding it here (after ApplyConfigurationsFromAssembly has already
        // run) keeps the same GETUTCDATE()-vs-Postgres split without duplicating the whole entity configuration.
        modelBuilder.Entity<WorkflowDefinition>()
            .Property(x => x.CreatedOnUtc)
            .HasDefaultValueSql(createdOnUtcDefaultSql);

        // A handful of entity configurations declare filtered-index/check-constraint predicates as raw SQL
        // using T-SQL's "[Column]"-bracket identifier quoting and integer 0/1 boolean literals — neither
        // parses on Postgres ("[" is a syntax error, and a boolean column can't be compared to an integer).
        // Re-declaring the same index/constraint here (matched by the same property set / constraint name)
        // overrides the predicate text from the per-entity configuration above, the same override pattern
        // used for WorkflowDefinition.CreatedOnUtc just above.
        if (isNpgsql)
        {
            modelBuilder.Entity<AllowedCorsOrigin>()
                .HasIndex(x => x.OriginUrl).IsUnique().HasFilter("\"IsDeleted\" = false");
            modelBuilder.Entity<EhrEndpoint>()
                .HasIndex(x => new { x.Vendor, x.VendorEndpointId }).IsUnique().HasFilter("\"IsDeleted\" = false");
            modelBuilder.Entity<SystemSetting>()
                .HasIndex(x => x.Key).IsUnique().HasFilter("\"IsDeleted\" = false");
            modelBuilder.Entity<User>()
                .HasIndex(x => x.ExternalUserId).IsUnique().HasFilter("\"IsDeleted\" = false");
            modelBuilder.Entity<ErrorLog>()
                .HasIndex(x => x.ErrorReferenceId).HasFilter("\"ErrorReferenceId\" IS NOT NULL");
            modelBuilder.Entity<PermissionAllocation>().ToTable(tb => tb.HasCheckConstraint(
                "CK_PermissionAllocations_RoleXorUser",
                "(\"RoleId\" IS NOT NULL AND \"UserId\" IS NULL) OR (\"RoleId\" IS NULL AND \"UserId\" IS NOT NULL)"));

            modelBuilder.Entity<CvxVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<HcpcsVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<Icd10PcsVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<Icd10Version>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<LoincVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<NdcVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<RxNormVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<SnomedVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            modelBuilder.Entity<UcumVersion>().HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");

            // TrmConceptConfiguration's SQL Server-only collation ("SQL_Latin1_General_CP1_CS_AS") and column
            // type ("nvarchar(max)") don't parse on Postgres. Clearing the collation back to the database
            // default is sufficient there — unlike SQL Server, Postgres's default collation already compares
            // text byte-for-byte (case-sensitive) for equality/uniqueness, which is the whole reason that
            // collation was added (see TrmConceptConfiguration's remarks on the UCUM "S"/"s" collision). "text"
            // is Npgsql's own equivalent of an unbounded column, same as nvarchar(max) is for SQL Server.
            modelBuilder.Entity<TrmConcept>().Property(x => x.CodeVal).UseCollation(null);
            modelBuilder.Entity<TrmConcept>().Property(x => x.Display).HasColumnType("text");
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
