using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Destinations;
using FHIRBridge.Runtime.Application.Abstractions.Persistence;
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

        services.AddHttpClient<EpicAccessTokenProvider>().AddMutualTls();
        services.AddHttpClient<OAuth2ClientCredentialsTokenProvider>().AddMutualTls();
        services.AddHttpClient<HealowAuthorizationCodeTokenProvider>().AddMutualTls();
        services.AddHttpClient<MeditechGreenfieldTokenProvider>().AddMutualTls();
        services.AddHttpClient<EpicFhirSourceClient>().AddMutualTls();
        // Composite picks the grant per source: Healow (auth-code + PKCE), MEDITECH Greenfield (confidential JSON),
        // SMART JWT (Epic), or OAuth2 client-credentials (Cerner/Allscripts/generic FHIR).
        services.AddScoped<CompositeFhirAccessTokenProvider>();
        services.AddScoped<IFhirAccessTokenProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<CompositeFhirAccessTokenProvider>());

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
