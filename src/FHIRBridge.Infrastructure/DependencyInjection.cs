using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Governance;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Infrastructure.Caching;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Blob;
using FHIRBridge.Infrastructure.Destinations.Delivery;
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

/// <summary>EF Core provider backing <see cref="Persistence.FHIRBridgeDbContext"/>, selected via the
/// "Database:Provider" config key. Defaults to <see cref="SqlServer"/> so every existing deployment
/// (which has no such key set) keeps behaving exactly as before.</summary>
public enum PersistenceProvider
{
    SqlServer,
    PostgreSql
}

public static class DependencyInjection
{
    public static IServiceCollection AddFHIRBridgeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Governance: log every outbound HTTP call's method/URL/status/duration (never headers/tokens/bodies).
        // Registered before the resilience handler below so its duration reflects the full retried call, not
        // just the final attempt.
        services.AddTransient<ApiRequestLoggingHandler>();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<ApiRequestLoggingHandler>());

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
            // HIPAA #15: fail fast rather than silently carrying cached FHIR tokens/scopes over plaintext Redis
            // outside Development. StackExchange.Redis's connection-string format uses an "ssl=true" option
            // rather than a URI scheme, so that's what's checked here — not a "rediss://" prefix.
            if (!redisConnectionString.Contains("ssl=true", StringComparison.OrdinalIgnoreCase) &&
                !Messaging.MessagingServiceCollectionExtensions.IsDevelopmentEnvironment())
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Redis must include 'ssl=true' outside Development — refusing to start with a plaintext Redis connection.");
            }

            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "fhirbridge:";
            });
        }

        // Field-level encryptor for the PHI-bearing execution-history columns (fetched/normalized/mapped payloads).
        // Registered unconditionally — it has no DB dependency of its own.
        services.AddSingleton<IPhiFieldEncryptor, AesGcmPhiFieldEncryptor>();

        // Endpoint health for destinations — registry over switch, one registration per DestinationType this
        // provider covers (see its remarks for which types those are). Registered unconditionally — no DB
        // dependency of its own; EndpointHealthCheckWorker resolves ISecretProvider/destinations lazily.
        // BlobStorage has its own dedicated provider below (its secret is no longer always a URL to HEAD).
        foreach (var destinationType in new[]
        {
            Domain.Enums.DestinationType.Csv, Domain.Enums.DestinationType.Excel,
            Domain.Enums.DestinationType.PowerBi, Domain.Enums.DestinationType.Snowflake, Domain.Enums.DestinationType.S3,
            Domain.Enums.DestinationType.Ndjson, Domain.Enums.DestinationType.Parquet, Domain.Enums.DestinationType.Tableau,
            Domain.Enums.DestinationType.Pdf, Domain.Enums.DestinationType.Avro, Domain.Enums.DestinationType.Protobuf,
        })
        {
            services.AddScoped<Application.Abstractions.Destinations.IDestinationHealthCheckProvider>(sp =>
                new Destinations.TargetReachabilityDestinationHealthCheckProvider(
                    destinationType, sp.GetRequiredService<ISecretProvider>(), sp.GetRequiredService<IHttpClientFactory>()));
        }

        // FhirRepository (e.g. Aidbox) and AzureFhirService (Azure Health Data Services — same FHIR R4 wire
        // behavior) both need an authenticated conformance check (GET {base}/metadata), not the anonymous
        // HEAD/directory check TargetReachabilityDestinationHealthCheckProvider does above — one provider
        // instance per DestinationType instead of joining that loop. Previously FhirRepository had no registered
        // health check at all.
        services.AddHttpClient(nameof(Destinations.FhirRepositoryHealthCheckProvider));
        foreach (var fhirLikeDestinationType in new[]
        {
            Domain.Enums.DestinationType.FhirRepository, Domain.Enums.DestinationType.AzureFhirService,
        })
        {
            services.AddScoped<Application.Abstractions.Destinations.IDestinationHealthCheckProvider>(sp =>
                new Destinations.FhirRepositoryHealthCheckProvider(
                    fhirLikeDestinationType,
                    sp.GetRequiredService<ISecretProvider>(),
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<Destinations.Auth.IFhirDestinationTokenProvider>(),
                    sp.GetRequiredService<Destinations.Auth.IAzureManagedIdentityFhirTokenProvider>()));
        }

        services.AddSingleton<BlobContainerClientCache>();
        services.AddScoped<IBlobContainerClientFactory, BlobContainerClientFactory>();
        services.AddScoped<Application.Abstractions.Destinations.IDestinationHealthCheckProvider, BlobStorageDestinationHealthCheckProvider>();

        // Registered unconditionally (before the in-memory/DB branch below) — it resolves
        // IAllowedCorsOriginRepository lazily through a scope, so it works against either repository.
        services.AddSingleton<IAllowedCorsOriginsCache, InProcessAllowedCorsOriginsCache>();

        // Same reasoning as above — DB-backed overrides for appsettings-derived runtime knobs (worker
        // cadence, thresholds, feature toggles). Registered unconditionally, works against either repository.
        services.AddSingleton<ISystemSettingsCache, InProcessSystemSettingsCache>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        services.AddSingleton<ITerminologySyncScheduleEvaluator, TerminologySyncScheduleEvaluator>();

        // Resolves a user's effective permission codes per request (DB-backed, short-lived cache) —
        // replaces embedding them as JWT claims, which overflowed the browser's access-token cookie once
        // a role's permission count grew into the hundreds (dynamically-discovered per-vendor/per-
        // destination-type codes). Registered unconditionally, works against either repository.
        services.AddMemoryCache();
        services.AddSingleton<IUserPermissionsProvider, CachedUserPermissionsProvider>();

        // "SSO Configurations" admin screen — reads/writes SAML + magic-link fields as SystemSetting
        // rows via the two services registered just above, so saves take effect without a restart.
        services.AddScoped<ISsoConfigurationsService, SsoConfigurationsService>();

        // Registered unconditionally — resolves against IUserAccessRepository, so it works identically whether
        // that's the in-memory or EF-backed implementation registered below.
        services.AddScoped<IUserDisplayNameResolver, UserDisplayNameResolver>();

        var connectionString = configuration.GetConnectionString("FHIRBridgeDb");
        var persistenceProvider = configuration.GetValue("Database:Provider", PersistenceProvider.SqlServer);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IConfigurationRepository, InMemoryConfigurationRepository>();
            services.AddSingleton<IUserAccessRepository, InMemoryUserAccessRepository>();
            services.AddSingleton<IConfiguredPipelineRunRepository, InMemoryConfiguredPipelineRunRepository>();
            services.AddSingleton<IPipelineRunRouteExecutionRepository, InMemoryPipelineRunRouteExecutionRepository>();
            services.AddSingleton<IExecutionResourceHistoryRecorder, InMemoryExecutionResourceHistoryRecorder>();
            services.AddSingleton<ISourceCapabilityRepository, InMemorySourceCapabilityRepository>();
            services.AddSingleton<IEhrEndpointRepository, InMemoryEhrEndpointRepository>();
            services.AddSingleton<IAllowedCorsOriginRepository, InMemoryAllowedCorsOriginRepository>();
            services.AddSingleton<ISystemSettingRepository, InMemorySystemSettingRepository>();
            services.AddSingleton<INotificationSettingsRepository, InMemoryNotificationSettingsRepository>();
            services.AddSingleton<IBrandConfigurationRepository, InMemoryBrandConfigurationRepository>();
            services.AddSingleton<ITenantRepository, InMemoryTenantRepository>();

            // No database: per-process idempotency. Fine for single-process dev; not multi-instance safe.
            services.AddSingleton<IProcessedMessageStore, InMemoryProcessedMessageStore>();

            // No database: nothing to persist governance events to, nothing to read them back from.
            services.AddSingleton<IGovernanceLogger, NullGovernanceLogger>();
            services.AddSingleton<IGovernanceQueryService, EmptyGovernanceQueryService>();
            services.AddSingleton<IErrorResolutionService, NullErrorResolutionService>();
            services.AddSingleton<IComplianceReportService, NullComplianceReportService>();
            services.AddSingleton<IAuditChainVerificationService, NullAuditChainVerificationService>();
            services.AddSingleton<IGovernanceLogArchiveWriter, NullGovernanceLogArchiveWriter>();
            services.AddScoped<ISystemHealthService, InMemorySystemHealthService>();
            services.AddScoped<ISchedulerSummaryService, InMemorySchedulerSummaryService>();
            services.AddScoped<IDataLineageService, InMemoryDataLineageService>();
            services.AddScoped<IAlertRuleService, InMemoryAlertRuleService>();
            services.AddScoped<IAlertEvaluationService, NullAlertEvaluationService>();
        }
        else
        {
            // Fallback actor source for audit stamping in hosts without an HTTP context (Worker, migrations).
            // The API host registers an HTTP-aware ICurrentUserService that takes precedence over this.
            services.TryAddSingleton<IAmbientActorContext, AmbientActorContext>();
            services.TryAddScoped<ICurrentUserService, SystemCurrentUserService>();
            services.AddScoped<AuditingSaveChangesInterceptor>();
            services.AddScoped<IGovernanceLogger, EfGovernanceLogger>();
            services.AddScoped<IGovernanceQueryService, EfGovernanceQueryService>();
            services.AddScoped<IErrorResolutionService, EfErrorResolutionService>();
            services.AddScoped<IAuditChainVerificationService, EfAuditChainVerificationService>();
            services.AddScoped<IComplianceReportService, QuestPdfComplianceReportService>();
            services.AddScoped<IGovernanceLogArchiveWriter, EfGovernanceLogArchiveWriter>();
            services.Configure<GovernanceArchiveOptions>(configuration.GetSection("Governance:Archive"));
            services.AddScoped<ISystemHealthService, EfSystemHealthService>();
            services.AddScoped<ISchedulerSummaryService, EfSchedulerSummaryService>();
            services.AddScoped<IDataLineageService, EfDataLineageService>();
            services.AddScoped<IAlertRuleService, EfAlertRuleService>();
            services.AddScoped<IAlertEvaluationService, EfAlertEvaluationService>();

            services.AddDbContext<FHIRBridgeDbContext>((sp, options) =>
            {
                switch (persistenceProvider)
                {
                    case PersistenceProvider.PostgreSql:
                        options.UseNpgsql(
                            connectionString,
                            npgsql => npgsql.MigrationsAssembly("FHIRBridge.Infrastructure.Migrations.PostgreSql"));
                        break;
                    default:
                        options.UseSqlServer(connectionString);
                        break;
                }

                options.AddInterceptors(sp.GetRequiredService<AuditingSaveChangesInterceptor>());
            });

            // Runtime RBAC reference-data bootstrapper (replaces the former migration HasData seed).
            // Only registered in the DB path — it provisions rows into FHIRBridgeDbContext. The in-memory
            // path self-seeds the same catalog in InMemoryUserAccessRepository's constructor.
            services.AddScoped<IRbacBootstrapper, RbacBootstrapper>();

            // EHR vendor endpoint-directory importers (currently just Epic's open.epic.com/Endpoints/R4) — only
            // registered in the DB path, same reasoning as IRbacBootstrapper above. Adding a new vendor's directory
            // is registering one more IEhrEndpointDirectorySeeder here; Program.cs runs every registered one.
            services.AddScoped<IEhrEndpointDirectorySeeder, EpicEndpointDirectorySeeder>();
            services.AddScoped<IEhrEndpointRepository, EfEhrEndpointRepository>();
            services.AddScoped<IAllowedCorsOriginRepository, EfAllowedCorsOriginRepository>();
            services.AddScoped<IUserFhirContextBindingRepository, EfUserFhirContextBindingRepository>();
            services.AddScoped<ISystemSettingRepository, EfSystemSettingRepository>();
            services.AddScoped<ISystemSettingsSeeder, SystemSettingsSeeder>();
            services.AddScoped<INotificationSettingsRepository, EfNotificationSettingsRepository>();
            services.AddScoped<IBrandConfigurationRepository, EfBrandConfigurationRepository>();
            services.AddScoped<ITenantRepository, EfTenantRepository>();

            services.AddScoped<IConfigurationRepository, EfConfigurationRepository>();
            services.AddScoped<ISchemaMappingRepository, EfSchemaMappingRepository>();
            services.AddScoped<ITransformationRuleRepository, EfTransformationRuleRepository>();
            services.AddScoped<IDeIdentificationProfileRepository, EfDeIdentificationProfileRepository>();
            services.AddScoped<IDeIdentificationProfileSeeder, DeIdentificationProfileSeeder>();
            services.AddScoped<IUserAccessRepository, EfUserAccessRepository>();
            services.AddScoped<IConfiguredPipelineRunRepository, EfConfiguredPipelineRunRepository>();
            services.AddScoped<IBulkExportJobRepository, EfBulkExportJobRepository>();
            services.Configure<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>(configuration.GetSection("BulkExport"));
            services.AddScoped<FHIRBridge.Runtime.Application.Workflows.Storage.IBulkExportPauseRecorder, FHIRBridge.Infrastructure.Workflows.BulkExportPauseRecorder>();
            services.AddScoped<IPipelineRunRouteExecutionRepository, EfPipelineRunRouteExecutionRepository>();
            services.AddScoped<EfExecutionResourceHistoryRecorder>();
            services.AddScoped<IExecutionResourceHistoryRecorder>(sp => sp.GetRequiredService<EfExecutionResourceHistoryRecorder>());
            services.AddScoped<IPurgeableStore>(sp => sp.GetRequiredService<EfExecutionResourceHistoryRecorder>());

            // Operations-log retention: every governance table EXCEPT the four immutable, 7-year HIPAA audit
            // tables (AuditLog, AuthenticationLog, SmartLaunchLog, DataAccessLog — deliberately never registered
            // here) is purgeable per the configured retention policy (see RetentionPurgeService/ConfiguredRetentionPolicyService).
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.SchedulerHistory>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "SchedulerHistory", x => x.RunTimeUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.RetryHistory>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "RetryHistory", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.ErrorLog>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "ErrorLog", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.ApiRequestLog>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "ApiRequestLog", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.ExportHistory>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "ExportHistory", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.NotificationHistory>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "NotificationHistory", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.ValidationFailureLog>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "ValidationFailureLog", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.EndpointHealthCheck>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "EndpointHealthCheck", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.SecurityEvent>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "SecurityEvent", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.AuthorizationLog>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "AuthorizationLog", x => x.OccurredOnUtc));
            services.AddScoped<IPurgeableStore>(sp => new Governance.GovernanceLogPurgeableStore<Domain.Entities.Governance.AlertHistoryEntry>(
                sp.GetRequiredService<FHIRBridgeDbContext>(), sp.GetRequiredService<IGovernanceLogArchiveWriter>(), "AlertHistoryEntry", x => x.FiredOnUtc));

            services.AddScoped<ISourceCapabilityRepository, EfSourceCapabilityRepository>();

            // Durable, multi-instance idempotency backed by the ProcessedMessages table.
            services.AddScoped<IProcessedMessageStore, EfProcessedMessageStore>();
        }

        // Phase 6A – Enterprise Global Exception Management. Registered for both the DB and in-memory paths
        // (depends only on IGovernanceLogger, which each branch registers, and the classifier). Scoped so it
        // composes with the scoped EfGovernanceLogger; the classifier is stateless and shared.
        services.AddSingleton<IExceptionClassifier>(_ => new DefaultExceptionClassifier());

        // Docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md §8 — one rule per known failure signature, registered
        // like any other strategy in this project (EHR vendor connectors, auth strategies) rather than grown as
        // a single central switch. Add a new destination/source failure signature by adding a rule here, not by
        // editing DefaultFailureDiagnosisClassifier.
        services.AddSingleton<IFailureDiagnosisRule, FHIRBridge.Infrastructure.Governance.SqlDestinationFailureDiagnosisRule>();
        services.AddSingleton<IFailureDiagnosisRule, FHIRBridge.Infrastructure.Governance.TokenEndpointFailureDiagnosisRule>();
        services.AddSingleton<IFailureDiagnosisRule, FHIRBridge.Infrastructure.Governance.BulkExportKickOffFailureDiagnosisRule>();
        services.AddSingleton<IFailureDiagnosisRule, FHIRBridge.Infrastructure.Governance.SftpDestinationFailureDiagnosisRule>();
        services.AddSingleton<IFailureDiagnosisRule, FHIRBridge.Infrastructure.Governance.MongoDestinationFailureDiagnosisRule>();
        services.AddSingleton<IFailureDiagnosisClassifier, DefaultFailureDiagnosisClassifier>();
        services.AddScoped<IGlobalExceptionManager, GlobalExceptionManager>();

        services.AddRuntimeInfrastructure(configuration);
        services.AddMessaging(configuration);
        services.AddScoped<IScheduleEvaluationService, ScheduleEvaluationService>();
        services.AddScoped<IScheduleDispatcher, ScheduleDispatcher>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITotpService, TotpService>();
        services.Configure<MfaOptions>(configuration.GetSection("Mfa"));

        // SSO token exchange (validate an external IdP token -> mint a FHIRBridge JWT). Providers are OFF
        // by default; the composite validator throws when a disabled provider is requested.
        services.Configure<EntraAuthenticationOptions>(configuration.GetSection("Authentication:Entra"));
        services.Configure<GoogleAuthenticationOptions>(configuration.GetSection("Authentication:Google"));
        services.AddSingleton<IProviderTokenValidator, EntraTokenValidator>();
        services.AddSingleton<IProviderTokenValidator, GoogleTokenValidator>();
        services.AddSingleton<IExternalTokenValidator, CompositeExternalTokenValidator>();

        // SAML 2.0 SSO (single configured IdP, system-wide — see FHIRBridge.Domain/README.md on why this
        // isn't per-tenant). Off by default; the ACS endpoint 404s until Authentication:Saml:Enabled is set.
        services.Configure<SamlAuthenticationOptions>(configuration.GetSection("Authentication:Saml"));
        services.AddSingleton<ISamlConfigurationProvider, SamlConfigurationProvider>();

        services.Configure<LocalAuthOptions>(configuration.GetSection("LocalAuth"));
        services.AddScoped<IEmailSender, Email.SmtpEmailSender>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();
        services.AddScoped<Application.Abstractions.Branding.IBrandConfigurationService, Application.Services.BrandConfigurationService>();
        services.AddScoped<Application.Abstractions.Tenancy.ITenantsService, Application.Services.TenantsService>();
        // Singleton, same reasoning as IUserPermissionsProvider/CachedUserPermissionsProvider — resolves
        // IUserAccessRepository lazily through a scope, so it works with either repository registration.
        services.AddSingleton<Application.Abstractions.Tenancy.ICurrentTenantResolver, Security.CachedCurrentTenantResolver>();
        services.AddHttpClient(nameof(SourceConnectionTestService));
        services.AddHttpClient(nameof(SourceCapabilityDiscoveryService));
        services.AddHttpClient(nameof(BackendAuthScopeProbeService));
        services.AddHttpClient(nameof(EpicEndpointDirectorySeeder));
        services.AddHttpClient(nameof(MappedRestApiDestinationWriter));
        services.AddHttpClient(nameof(MappedFhirRepositoryDestinationWriter));
        services.AddHttpClient(nameof(Destinations.Auth.FhirDestinationOAuth2TokenProvider));
        services.AddScoped<Destinations.Auth.IFhirDestinationTokenProvider>(sp =>
            new Destinations.Auth.FhirDestinationOAuth2TokenProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Destinations.Auth.FhirDestinationOAuth2TokenProvider)),
                sp.GetRequiredService<IFhirAccessTokenCache>()));
        // Managed-identity alternative to the client-credentials flow above (e.g. Azure Health Data Services'
        // FHIR API from an Azure-hosted Worker/App Service) — no HttpClient of its own, DefaultAzureCredential
        // manages its own token-endpoint calls (IMDS / Entra) internally.
        services.AddScoped<Destinations.Auth.IAzureManagedIdentityFhirTokenProvider>(sp =>
            new Destinations.Auth.AzureManagedIdentityFhirTokenProvider(sp.GetRequiredService<IFhirAccessTokenCache>()));
        services.AddHttpClient(nameof(MappedExcelDestinationWriter));
        services.AddHttpClient(nameof(MappedCsvDestinationWriter));
        services.AddHttpClient(nameof(MappedSnowflakeDestinationWriter));
        services.AddHttpClient(nameof(MappedPowerBiDestinationWriter));
        services.AddHttpClient(nameof(FhirTerminologyLookupService));
        services.AddHttpClient(nameof(LoincReleaseClient));
        services.AddHttpClient(nameof(UtsReleaseClient));
        services.AddHttpClient(nameof(NdcReleaseClient));
        services.AddHttpClient(nameof(ReleaseFreshnessChecker));
        services.AddHttpClient(nameof(UcumReleaseClient));
        services.AddHttpClient(nameof(FhirTerminologyTranslationService));

        services.AddSingleton<MappedInMemoryDestinationBuffer>();
        services.AddScoped<MappedInMemoryDestinationWriter>();
        services.AddScoped<MappedSqlServerDestinationWriter>();
        services.AddScoped<MappedBlobStorageDestinationWriter>();
        services.AddScoped<MappedRestApiDestinationWriter>();
        services.AddScoped<MappedFhirRepositoryDestinationWriter>();
        services.AddScoped<MappedExcelDestinationWriter>();
        services.AddScoped<MappedCsvDestinationWriter>();
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
        services.AddScoped<MappedMongoDestinationWriter>();
        services.AddHttpClient(nameof(MedplumTokenProvider));
        services.AddHttpClient(nameof(MappedMedplumDestinationWriter));
        // The Medplum token provider signs a private_key_jwt assertion with the same RS384 factory Epic uses. It is
        // normally registered by the runtime-infrastructure DI; TryAdd makes the configured plane self-sufficient
        // (no-op when the runtime DI already registered it).
        services.TryAddSingleton<IBackendServicesJwtFactory, FHIRBridge.Runtime.Infrastructure.Auth.BackendServicesJwtFactory>();
        services.AddSingleton<IMedplumTokenProvider, MedplumTokenProvider>();
        services.AddScoped<MappedMedplumDestinationWriter>();
        foreach (var registration in ConfiguredDestinationWriterFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        services.AddScoped<IConfiguredDestinationWriterFactory, ConfiguredDestinationWriterFactory>();

        // Generated-file delivery strategies (Download/Email/SFTP/Download-link) — currently used by the CSV writer
        // only, but format-agnostic so future Excel/PDF/XML writers reuse the same four without new plumbing.
        services.AddScoped<DownloadDeliveryStrategy>();
        services.AddScoped<EmailDeliveryStrategy>();
        services.AddScoped<SftpDeliveryStrategy>();
        services.AddScoped<DownloadUrlDeliveryStrategy>();
        foreach (var registration in ArtifactDeliveryStrategyFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        services.AddScoped<IArtifactDeliveryStrategyFactory, ArtifactDeliveryStrategyFactory>();
        services.Configure<GeneratedFileDownloadOptions>(configuration.GetSection("GeneratedFileDownload"));
        services.AddSingleton<IGeneratedFileDownloadLinkService, GeneratedFileDownloadLinkService>();
        services.AddScoped<IDestinationSchemaService, SqlDestinationSchemaService>();
        services.AddScoped<ICsvDestinationConnectionTestService, SftpDestinationConnectionTestService>();
        services.AddHttpClient(nameof(Destinations.FhirDestinationConnectionTestService));
        services.AddScoped<IFhirDestinationConnectionTestService>(sp =>
            new Destinations.FhirDestinationConnectionTestService(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<Destinations.Auth.IFhirDestinationTokenProvider>(),
                sp.GetRequiredService<Destinations.Auth.IAzureManagedIdentityFhirTokenProvider>()));

        services.AddHttpClient(nameof(Destinations.MedplumDestinationConnectionTestService));
        services.AddScoped<IMedplumDestinationConnectionTestService>(sp =>
            new Destinations.MedplumDestinationConnectionTestService(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IMedplumTokenProvider>()));

        services.AddScoped<IMongoDestinationConnectionTestService, Destinations.MongoDestinationConnectionTestService>();
        services.AddScoped<IBlobDestinationConnectionTestService, Destinations.BlobDestinationConnectionTestService>();

        foreach (var registration in MappingSchemaProviderFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        services.AddScoped<SqlServerMappingSchemaProvider>();
        services.AddScoped<MySqlMappingSchemaProvider>();
        services.AddScoped<PostgreSqlMappingSchemaProvider>();
        services.AddScoped<IMappingSchemaProviderFactory, MappingSchemaProviderFactory>();
        // Read-back of a capped row sample from a relational destination table ("View destination data").
        services.AddScoped<IDestinationDataService, SqlDestinationDataService>();
        // Option A: workflow source nodes reference a real SourceConnection by id; this resolves it to the runtime
        // FHIR source config (base URL + auth + token) at run time so a graph run matches a route run.
        services.AddScoped<Runtime.Application.Abstractions.Sources.ISourceConnectionRuntimeResolver,
            Sources.SourceConnectionRuntimeResolver>();
        // Records a Backend System source's incremental-sync cursor after a successful run — the write-back half
        // of the resolver above.
        services.AddScoped<Runtime.Application.Abstractions.Sources.ISourceConnectionSyncCursorStore,
            Sources.SourceConnectionSyncCursorStore>();
        // Phase 2: distributed-cache decorators over the local→FHIR terminology composites. Lookups/translations are
        // stable per code-system/map version and repeated across a run, so positive results are cached for this TTL.
        var terminologyCacheTtl = TimeSpan.FromMinutes(
            configuration.GetValue<int?>("Caching:TerminologyTtlMinutes") ?? 60);

        services.AddSingleton<LocalTerminologyLookupService>();
        services.AddScoped<LoincTerminologyLookupService>();
        services.AddScoped<Icd10TerminologyLookupService>();
        services.AddScoped<SnomedTerminologyLookupService>();
        services.AddScoped<RxNormTerminologyLookupService>();
        services.AddScoped<ILoincReleaseClient, LoincReleaseClient>();
        services.AddScoped<ILoincSynchronizationService, LoincSynchronizationService>();
        services.AddScoped<ISnomedImportService, SnomedImportService>();
        services.AddScoped<IIcd10ImportService, Icd10ImportService>();
        services.AddScoped<IRxNormImportService, RxNormImportService>();
        services.AddScoped<IUtsReleaseClient, UtsReleaseClient>();
        services.AddScoped<IRxNormSynchronizationService, RxNormSynchronizationService>();
        services.AddScoped<ISnomedSynchronizationService, SnomedSynchronizationService>();
        services.AddScoped<INdcImportService, NdcImportService>();
        services.AddScoped<INdcReleaseClient, NdcReleaseClient>();
        services.AddScoped<INdcSynchronizationService, NdcSynchronizationService>();
        services.AddScoped<IIcd10PcsImportService, Icd10PcsImportService>();
        services.AddScoped<IHcpcsImportService, HcpcsImportService>();
        services.AddScoped<IReleaseFreshnessChecker, ReleaseFreshnessChecker>();
        services.AddScoped<ICvxImportService, CvxImportService>();
        services.AddScoped<IUcumImportService, UcumImportService>();
        services.AddScoped<IUcumReleaseClient, UcumReleaseClient>();
        services.AddScoped<IUcumSynchronizationService, UcumSynchronizationService>();
        // Runs LOINC/SNOMED/ICD-10/RxNorm imports off the request thread — see TerminologyImportChannel's
        // remarks for why this stays in-process rather than going through the Worker/MassTransit.
        services.AddSingleton<TerminologyImportChannel>();
        services.AddHostedService<TerminologyImportBackgroundService>();
        services.AddScoped<FhirTerminologyLookupService>();
        services.AddScoped<CompositeTerminologyLookupService>();
        services.AddScoped<ITerminologyLookupService>(sp => new CachingTerminologyLookupService(
            sp.GetRequiredService<CompositeTerminologyLookupService>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<ISystemSettingsCache>(),
            terminologyCacheTtl));
        services.AddSingleton<LocalTerminologyTranslationService>();
        services.AddScoped<FhirTerminologyTranslationService>();
        services.AddScoped<CompositeTerminologyTranslationService>();
        services.AddScoped<ITerminologyTranslationService>(sp => new CachingTerminologyTranslationService(
            sp.GetRequiredService<CompositeTerminologyTranslationService>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<ISystemSettingsCache>(),
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
        services.AddScoped<IResourceNormalizationStep, IdentifierFlatteningNormalizationStep>();
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
        // HIPAA #2: de-identification now actually triggers for destinations with a DeIdentificationProfileId set.
        services.AddScoped<IGovernanceRule, DestinationSensitivityGovernanceRule>();
        services.AddScoped<IGovernancePolicyService, CompositeGovernancePolicyService>();

        // G3: real HIPAA Safe Harbor de-identification, sourced from the profile's pre-mapping TransformationRule
        // rows (overrides the Application pass-through stub).
        services.AddScoped<IDeIdentificationService, SafeHarborDeIdentificationService>();
        services.AddScoped<IDeIdentificationProfileService, DeIdentificationProfileService>();

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
        // App-level secrets (JWT signing key, download-link signing secret) — auto-generated on first boot
        // via AppSecretProvisioner and cached here for the app's lifetime. See AppSecretAccessor's remarks.
        services.AddSingleton<AppSecretAccessor>();
        services.AddSingleton<IAppSecretAccessor>(sp => sp.GetRequiredService<AppSecretAccessor>());
        // B1: app-provisioned secrets. DbSecretStore/CompositeSecretProvider need FHIRBridgeDbContext, which
        // only exists on the SQL-backed path below — the InMemory path gets a process-local stand-in instead.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<InMemorySecretStore>();
            services.AddSingleton<ISecretWriter>(sp => sp.GetRequiredService<InMemorySecretStore>());
            services.AddSingleton<ISecretProvider>(sp => sp.GetRequiredService<InMemorySecretStore>());
            services.AddSingleton<IAppSecretMetadataProvider>(sp => sp.GetRequiredService<InMemorySecretStore>());
        }
        else
        {
            services.AddScoped<DbSecretStore>();
            services.AddScoped<ISecretWriter>(sp => sp.GetRequiredService<DbSecretStore>());
            services.AddScoped<ISecretProvider, CompositeSecretProvider>();
            services.AddScoped<IAppSecretMetadataProvider>(sp => sp.GetRequiredService<DbSecretStore>());
        }

        services.AddScoped<IAppSecretsAdminService, AppSecretsAdminService>();
        services.AddScoped<ILoincConfigurationService, LoincConfigurationService>();
        services.AddScoped<IRxNormConfigurationService, RxNormConfigurationService>();
        services.AddScoped<ISnomedConfigurationService, SnomedConfigurationService>();
        services.AddScoped<INdcConfigurationService, NdcConfigurationService>();
        services.AddScoped<IUcumConfigurationService, UcumConfigurationService>();
        // Needs only IDataProtectionProvider (registered app-wide in Program.cs), not FHIRBridgeDbContext — works
        // the same on both the SQL-backed and InMemory paths above.
        services.AddSingleton<IProvisionedSecretDecryptor, ProvisionedSecretDecryptor>();

        services.AddScoped<IConfiguredPipelineService, ConfiguredPipelineService>();

        // Synchronous patient-scoped aggregation read. Bound from "PatientAggregation"; defaults apply when absent.
        services.AddSingleton(
            configuration.GetSection(PatientAggregationOptions.SectionName).Get<PatientAggregationOptions>()
            ?? new PatientAggregationOptions());
        services.AddScoped<IPatientAggregationService, PatientAggregationService>();
        services.AddScoped<IFhirSubscriptionManagementService, FhirSubscriptionManagementService>();
        services.AddScoped<ISourceConnectionTestService, SourceConnectionTestService>();
        services.AddSingleton<ILaunchTokenProtector, DataProtectionLaunchTokenProtector>();
        services.AddScoped<IInteractiveSourceAuthorizationService, InteractiveSourceAuthorizationService>();
        services.AddScoped<IEpicSourceConnectionScopeSyncService, EpicSourceConnectionScopeSyncService>();
        services.AddScoped<ISourceCapabilityDiscoveryService, SourceCapabilityDiscoveryService>();
        services.AddScoped<ISourceEndpointProbeService, SourceEndpointProbeService>();
        services.AddScoped<IBackendAuthScopeProbeService, BackendAuthScopeProbeService>();
        services.AddScoped<ISourceJwksService, SourceJwksService>();
        services.AddScoped<ISigningKeyGenerationService, SigningKeyGenerationService>();
        var healthChecksBuilder = services.AddHealthChecks()
            .AddCheck<KeyVaultConfigurationHealthCheck>("keyvault");

        // TDE (Transparent Data Encryption) is a SQL Server / Azure SQL-only concept — there is no PostgreSQL
        // equivalent, so it's only registered on the SqlServer path.
        if (persistenceProvider == PersistenceProvider.PostgreSql)
        {
            healthChecksBuilder.AddCheck<PostgresConnectionHealthCheck>("postgresql");
        }
        else
        {
            healthChecksBuilder
                .AddCheck<SqlServerConnectionHealthCheck>("sqlserver")
                .AddCheck<SqlServerTdeHealthCheck>("sqlserver-tde");
        }

        return services;
    }
}
