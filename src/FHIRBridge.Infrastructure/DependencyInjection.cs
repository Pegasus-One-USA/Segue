using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Infrastructure.Audit;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Health;
using FHIRBridge.Infrastructure.Messaging;
using FHIRBridge.Infrastructure.Normalization;
using FHIRBridge.Infrastructure.Aggregation;
using FHIRBridge.Infrastructure.Normalization.Steps;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Pipeline;
using FHIRBridge.Infrastructure.Scheduling;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.Infrastructure.Terminology;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace FHIRBridge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFHIRBridgeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Phase 1.2: standard resilience (retry + circuit breaker + attempt/total timeouts) on EVERY outbound
        // HttpClient (EHR sources, REST/FHIR-repo/blob/S3/SFTP-N/A/terminology/source-test). Applied as a client
        // default so all IHttpClientFactory clients are covered. Timeouts are generous so legitimate long FHIR
        // search/bulk-export calls aren't cut off; retries are transient-fault only.
        services.ConfigureHttpClientDefaults(http =>
            http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
            }));

        // Phase 2: distributed cache. Registered FIRST so it wins the IDistributedCache TryAdd over the memory fallback
        // AddRuntimeInfrastructure registers for standalone use. Redis when ConnectionStrings:Redis is set (Redis
        // container in dev, Azure Cache for Redis in prod); in-process distributed memory otherwise. Backs the source
        // access-token cache (shared API+Worker, survives restart) and the terminology lookup/translate caches below.
        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "fhirbridge:";
            });
        }

        var connectionString = configuration.GetConnectionString("FHIRBridgeDb");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<ITenantConfigurationRepository, InMemoryTenantConfigurationRepository>();
            services.AddSingleton<IUserAccessRepository, InMemoryUserAccessRepository>();
            services.AddSingleton<IOperationalAuditService, InMemoryOperationalAuditService>();
            services.AddSingleton<IUserActivityAuditService, InMemoryUserActivityAuditService>();
            services.AddSingleton<IConfiguredPipelineRunRepository, InMemoryConfiguredPipelineRunRepository>();
            services.AddSingleton<ISourceCapabilityRepository, InMemorySourceCapabilityRepository>();

            // No database: per-process idempotency. Fine for single-process dev; not multi-instance safe.
            services.AddSingleton<IProcessedMessageStore, InMemoryProcessedMessageStore>();

            // G2: in-memory lineage store/query/purge. Not durable across restarts (dev only).
            services.AddSingleton<InMemoryLineageStore>();
            services.AddSingleton<ILineageStore>(sp => sp.GetRequiredService<InMemoryLineageStore>());
            services.AddSingleton<ILineageQueryService>(sp => sp.GetRequiredService<InMemoryLineageStore>());
            services.AddSingleton<IPurgeableStore>(sp => sp.GetRequiredService<InMemoryLineageStore>());
        }
        else
        {
            // Fallback actor source for audit stamping in hosts without an HTTP context (Worker, migrations).
            // The API host registers an HTTP-aware ICurrentUserService that takes precedence over this.
            services.TryAddScoped<ICurrentUserService, SystemCurrentUserService>();
            services.AddScoped<AuditingSaveChangesInterceptor>();

            services.AddDbContext<FHIRBridgeDbContext>((sp, options) =>
            {
                options.UseSqlServer(connectionString);
                options.AddInterceptors(sp.GetRequiredService<AuditingSaveChangesInterceptor>());
            });

            services.AddScoped<ITenantConfigurationRepository, EfTenantConfigurationRepository>();
            services.AddScoped<IUserAccessRepository, EfUserAccessRepository>();
            services.AddScoped<IOperationalAuditService, EfOperationalAuditService>();
            services.AddScoped<IUserActivityAuditService, EfUserActivityAuditService>();
            services.AddScoped<IConfiguredPipelineRunRepository, EfConfiguredPipelineRunRepository>();
            services.AddScoped<ISourceCapabilityRepository, EfSourceCapabilityRepository>();

            // Durable, multi-instance idempotency backed by the ProcessedMessages table.
            services.AddScoped<IProcessedMessageStore, EfProcessedMessageStore>();

            // G2: durable, EF-backed lineage store/query/purge (ResourceLineageEntries table).
            services.AddScoped<EfLineageStore>();
            services.AddScoped<ILineageStore>(sp => sp.GetRequiredService<EfLineageStore>());
            services.AddScoped<ILineageQueryService>(sp => sp.GetRequiredService<EfLineageStore>());
            services.AddScoped<IPurgeableStore>(sp => sp.GetRequiredService<EfLineageStore>());
        }

        services.AddRuntimeInfrastructure(configuration);
        services.AddMessaging(configuration);
        services.AddScoped<IScheduleEvaluationService, ScheduleEvaluationService>();
        services.AddScoped<IScheduleDispatcher, ScheduleDispatcher>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddScoped<IIdentitySeedService, LocalIdentitySeedService>();
        services.AddScoped<IFhirAccessTokenAuditSink, FhirAccessTokenAuditSink>();
        services.AddHttpClient(nameof(SourceConnectionTestService));
        services.AddHttpClient(nameof(SourceCapabilityDiscoveryService));
        services.AddHttpClient(nameof(MappedBlobStorageDestinationWriter));
        services.AddHttpClient(nameof(MappedRestApiDestinationWriter));
        services.AddHttpClient(nameof(MappedFhirRepositoryDestinationWriter));
        services.AddHttpClient(nameof(MappedExcelDestinationWriter));
        services.AddHttpClient(nameof(MappedSnowflakeDestinationWriter));
        services.AddHttpClient(nameof(MappedPowerBiDestinationWriter));
        services.AddHttpClient(nameof(FhirTerminologyLookupService));
        services.AddHttpClient(nameof(FhirTerminologyTranslationService));

        services.AddSingleton<MappedInMemoryDestinationBuffer>();
        services.AddScoped<MappedInMemoryDestinationWriter>();
        services.AddScoped<MappedSqlServerDestinationWriter>();
        services.AddScoped<MappedBlobStorageDestinationWriter>();
        services.AddScoped<MappedRestApiDestinationWriter>();
        services.AddScoped<MappedFhirRepositoryDestinationWriter>();
        services.AddScoped<MappedExcelDestinationWriter>();
        services.AddScoped<MappedSnowflakeDestinationWriter>();
        services.AddScoped<MappedPowerBiDestinationWriter>();
        services.AddScoped<MappedPostgreSqlDestinationWriter>();
        services.AddScoped<MappedMySqlDestinationWriter>();
        services.AddHttpClient(nameof(MappedS3DestinationWriter));
        services.AddScoped<MappedS3DestinationWriter>();
        services.AddHttpClient(nameof(MappedNdjsonDestinationWriter));
        services.AddScoped<MappedNdjsonDestinationWriter>();
        services.AddHttpClient(nameof(MappedParquetDestinationWriter));
        services.AddScoped<MappedParquetDestinationWriter>();
        services.AddScoped<MappedSftpDestinationWriter>();
        services.AddHttpClient(nameof(MappedTableauDestinationWriter));
        services.AddScoped<MappedTableauDestinationWriter>();
        services.AddScoped<MappedPdfDestinationWriter>();
        services.AddHttpClient(nameof(MappedAvroDestinationWriter));
        services.AddScoped<MappedAvroDestinationWriter>();
        services.AddHttpClient(nameof(MappedProtobufDestinationWriter));
        services.AddScoped<MappedProtobufDestinationWriter>();
        services.AddHttpClient(nameof(MappedDatabricksDestinationWriter));
        services.AddScoped<MappedDatabricksDestinationWriter>();
        foreach (var registration in ConfiguredDestinationWriterFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        services.AddScoped<IConfiguredDestinationWriterFactory, ConfiguredDestinationWriterFactory>();
        services.AddScoped<IDestinationSchemaService, SqlDestinationSchemaService>();
        // Phase 2: distributed-cache decorators over the local→FHIR terminology composites. Lookups/translations are
        // stable per code-system/map version and repeated across a run, so positive results are cached for this TTL.
        var terminologyCacheTtl = TimeSpan.FromMinutes(
            configuration.GetValue<int?>("Caching:TerminologyTtlMinutes") ?? 60);

        services.AddSingleton<LocalTerminologyLookupService>();
        services.AddScoped<FhirTerminologyLookupService>();
        services.AddScoped<CompositeTerminologyLookupService>();
        services.AddScoped<ITerminologyLookupService>(sp => new CachingTerminologyLookupService(
            sp.GetRequiredService<CompositeTerminologyLookupService>(),
            sp.GetRequiredService<IDistributedCache>(),
            terminologyCacheTtl));
        services.AddSingleton<LocalTerminologyTranslationService>();
        services.AddScoped<FhirTerminologyTranslationService>();
        services.AddScoped<CompositeTerminologyTranslationService>();
        services.AddScoped<ITerminologyTranslationService>(sp => new CachingTerminologyTranslationService(
            sp.GetRequiredService<CompositeTerminologyTranslationService>(),
            sp.GetRequiredService<IDistributedCache>(),
            terminologyCacheTtl));

        // ValueSet/$validate-code (US Core required-binding validation) and ValueSet/$expand, Local -> FHIR.
        services.AddHttpClient(nameof(FhirTerminologyValidationService));
        services.AddHttpClient(nameof(FhirTerminologyExpansionService));
        services.AddSingleton<LocalTerminologyValidationService>();
        services.AddScoped<FhirTerminologyValidationService>();
        services.AddScoped<ITerminologyValidationService, CompositeTerminologyValidationService>();
        services.AddSingleton<LocalTerminologyExpansionService>();
        services.AddScoped<FhirTerminologyExpansionService>();
        services.AddScoped<ITerminologyExpansionService, CompositeTerminologyExpansionService>();
        services.AddScoped<IMappedRecordNormalizationService, TerminologyMappedRecordNormalizationService>();

        // Per-resource normalization pipeline (Phase D flatten -> B validate -> C score -> E match).
        // Overrides the Application-layer PassThrough default because Infrastructure is registered last.
        services.AddScoped<IResourceNormalizationStep, ExtensionFlatteningNormalizationStep>();
        services.AddScoped<IResourceNormalizationStep, UsCoreValidationNormalizationStep>();
        services.AddScoped<IResourceNormalizationStep, DataQualityScoringNormalizationStep>();
        services.AddScoped<IResourceNormalizationStep, PatientMatchingNormalizationStep>();
        services.AddScoped<IResourceNormalizationService, CompositeResourceNormalizationService>();

        // FHIR Patient/$match (MPI). Enabled when PatientMatch:BaseUrl is configured; otherwise deterministic matching.
        services.AddHttpClient(nameof(FhirPatientMatchService));
        services.AddScoped<IPatientMatchService, FhirPatientMatchService>();

        // HL7 v2 MLLP ingestion (Signal layer). Options are bound by the Worker host; the listener itself is a
        // hosted service registered in the Worker, and resolves this processor per message.
        services.AddScoped<Hl7v2.Hl7MessageProcessor>();

        // Governance: composable rule-based policy evaluation. Overrides the Application-layer default because
        // Infrastructure is registered last. Per-resource-type RBAC (G1) is the first rule; consent (G5) is another.
        services.AddSingleton<IResourceTypeAccessPolicy, ConfiguredResourceTypeAccessPolicy>();
        services.AddScoped<IGovernanceRule, ResourceTypeAccessGovernanceRule>();
        services.AddSingleton<IConsentService, ConfiguredConsentService>();
        services.AddScoped<IGovernanceRule, ConsentGovernanceRule>();
        services.AddScoped<IGovernancePolicyService, CompositeGovernancePolicyService>();

        // G3: real HIPAA Safe Harbor de-identification (overrides the Application pass-through stub).
        services.AddScoped<IDeIdentificationService, SafeHarborDeIdentificationService>();

        // G4: configurable retention + a purge service over purgeable stores (immutable audit is never purged).
        // The lineage store (in-memory or EF-backed) is registered as IPurgeableStore in the DB-mode branch above.
        services.AddScoped<IRetentionPolicyService, ConfiguredRetentionPolicyService>();
        services.AddScoped<IRetentionPurgeService, RetentionPurgeService>();

        // Anomaly-detection thresholds/toggles. Bound from the "AnomalyDetection" section; defaults apply when absent.
        services.AddSingleton(
            configuration.GetSection(AnomalyDetectionOptions.SectionName).Get<AnomalyDetectionOptions>()
            ?? new AnomalyDetectionOptions());

        // Incremental ("since last run") source extraction. Bound from "IncrementalSync"; defaults apply when absent.
        services.AddSingleton(
            configuration.GetSection(IncrementalSyncOptions.SectionName).Get<IncrementalSyncOptions>()
            ?? new IncrementalSyncOptions());

        // Expert Determination (k-anonymity) set-level de-identification. Bound from "ExpertDetermination";
        // disabled by default (it suppresses records), so it is a passthrough until explicitly enabled.
        services.AddSingleton(
            configuration.GetSection(ExpertDeterminationOptions.SectionName).Get<ExpertDeterminationOptions>()
            ?? new ExpertDeterminationOptions());
        services.AddScoped<IDataSetDeIdentificationService, KAnonymityDeIdentificationService>();

        services.AddSingleton<ConfigurationSecretProvider>();
        services.AddSingleton<AzureKeyVaultSecretProvider>();
        services.AddScoped<ISecretProvider, CompositeSecretProvider>();
        services.AddScoped<IConfiguredPipelineService, ConfiguredPipelineService>();

        // Synchronous patient-scoped aggregation read. Bound from "PatientAggregation"; defaults apply when absent.
        services.AddSingleton(
            configuration.GetSection(PatientAggregationOptions.SectionName).Get<PatientAggregationOptions>()
            ?? new PatientAggregationOptions());
        services.AddScoped<IPatientAggregationService, PatientAggregationService>();
        services.AddScoped<IFhirSubscriptionManagementService, FhirSubscriptionManagementService>();
        services.AddScoped<ISourceConnectionTestService, SourceConnectionTestService>();
        services.AddScoped<ISourceCapabilityDiscoveryService, SourceCapabilityDiscoveryService>();
        services.AddScoped<ILineageTracker, OperationalAuditLineageTracker>();
        services.AddHealthChecks()
            .AddCheck<SqlServerConnectionHealthCheck>("sqlserver")
            .AddCheck<KeyVaultConfigurationHealthCheck>("keyvault");

        return services;
    }
}
