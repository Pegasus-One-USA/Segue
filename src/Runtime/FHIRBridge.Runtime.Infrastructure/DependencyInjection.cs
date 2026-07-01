using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Destinations;
using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Infrastructure.Applications;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FHIRBridge.Runtime.Infrastructure.Destinations;
using FHIRBridge.Runtime.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRuntimeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IPipelineRunStore, InMemoryPipelineRunStore>();
        services.AddSingleton<InMemoryDestinationBuffer>();
        services.Configure<EpicFhirClientOptions>(
            configuration.GetSection("Runtime:Epic"));

        services.AddSingleton<IBackendServicesJwtFactory, BackendServicesJwtFactory>();
        services.AddSingleton<IFhirAccessTokenAuditSink, NoOpFhirAccessTokenAuditSink>();

        // Phase 2: source access tokens are cached in the distributed cache (Redis when configured, in-process memory
        // otherwise — registered by the composing host). TryAdd a memory fallback so this assembly also works when wired
        // standalone (e.g. Runtime.Api), without overriding a Redis IDistributedCache already registered upstream.
        services.AddDistributedMemoryCache();
        services.AddSingleton<IFhirAccessTokenCache, DistributedFhirAccessTokenCache>();

        // mTLS + connection pooling for outbound FHIR connections.
        services.Configure<MutualTlsOptions>(configuration.GetSection("Connectivity:Mtls"));
        services.Configure<FhirConnectionPoolOptions>(configuration.GetSection("Connectivity:ConnectionPool"));
        services.AddSingleton<MutualTlsCertificateProvider>();

        services.AddSingleton<IFhirAuthorizationCodeTokenStore, InMemoryFhirAuthorizationCodeTokenStore>();
        // Short-lived state of in-flight interactive OAuth sign-ins, between the authorize redirect and the callback.
        services.AddSingleton<IOAuthAuthorizationStateStore, InMemoryOAuthAuthorizationStateStore>();

        services.AddHttpClient<EpicAccessTokenProvider>().AddMutualTls();
        services.AddHttpClient<OAuth2ClientCredentialsTokenProvider>().AddMutualTls();
        // Interactive SMART (authorization-code + PKCE): the vendor-neutral provider backs the application-type axis
        // (EHR launch / standalone / patient); Healow + Epic interactive pin the provider name for their audit trails.
        services.AddHttpClient<SmartAuthorizationCodeTokenProvider>().AddMutualTls();
        services.AddHttpClient<HealowAuthorizationCodeTokenProvider>().AddMutualTls();
        services.AddHttpClient<EpicInteractiveTokenProvider>().AddMutualTls();
        services.AddHttpClient<MeditechGreenfieldTokenProvider>().AddMutualTls();
        services.AddHttpClient<EpicFhirSourceClient>().AddMutualTls();

        // Application-type axis: each ApplicationType maps to a strategy that owns its grant/launch flow, validation
        // and descriptor. Resolved from the registry (never a switch — enforced by ApplicationTypeDispatchTests).
        // Scoped, because the strategies depend on the HttpClient-backed token providers.
        services.AddScoped<ISourceApplicationStrategy, BackendServicesApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategy, EhrLaunchApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategy, StandaloneApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategy, PatientApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategyRegistry, SourceApplicationStrategyRegistry>();

        // Composite picks the grant per source: the application-type strategy (registry) when set, else legacy
        // inference — Healow (auth-code + PKCE), MEDITECH Greenfield (confidential JSON), SMART JWT (Epic), or OAuth2
        // client-credentials (Cerner/Allscripts/generic FHIR).
        services.AddScoped<CompositeFhirAccessTokenProvider>();
        services.AddScoped<IFhirAccessTokenProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<CompositeFhirAccessTokenProvider>());

        // The interactive authorization-code round-trip (authorize + callback exchange) is served by the
        // vendor-neutral SMART provider.
        services.AddScoped<IInteractiveAuthorizationFlow>(serviceProvider =>
            serviceProvider.GetRequiredService<SmartAuthorizationCodeTokenProvider>());

        services.AddScoped<SampleFhirSourceClient>();
        foreach (var registration in FhirSourceClientFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        services.AddScoped<IFhirSourceClientFactory, FhirSourceClientFactory>();

        services.AddHttpClient<FhirRestSubscriptionClient>().AddMutualTls();
        services.AddScoped<IFhirSubscriptionClient>(serviceProvider =>
            serviceProvider.GetRequiredService<FhirRestSubscriptionClient>());

        services.Configure<FhirBulkExportOptions>(configuration.GetSection("Runtime:BulkExport"));
        services.AddHttpClient<FhirRestBulkExportClient>().AddMutualTls();
        services.AddScoped<IFhirBulkExportClient>(serviceProvider =>
            serviceProvider.GetRequiredService<FhirRestBulkExportClient>());

        services.AddScoped<SqlServerDestinationWriter>();
        services.AddScoped<InMemoryDestinationWriter>();
        foreach (var registration in DestinationWriterFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        services.AddScoped<IDestinationWriterFactory, DestinationWriterFactory>();

        return services;
    }

    // Configures an HttpClient's primary handler to present the mTLS client certificate when enabled.
    private static IHttpClientBuilder AddMutualTls(this IHttpClientBuilder builder)
    {
        return builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            serviceProvider.GetRequiredService<MutualTlsCertificateProvider>().CreatePrimaryHandler());
    }
}
