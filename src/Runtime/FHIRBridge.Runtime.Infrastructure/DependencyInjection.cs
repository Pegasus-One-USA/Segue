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
using Microsoft.Extensions.Logging;

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

        // Phase 2: source access tokens are cached in the distributed cache (Redis when configured, in-process memory
        // otherwise — registered by the composing host). TryAdd a memory fallback so this assembly also works when wired
        // standalone (e.g. Runtime.Api), without overriding a Redis IDistributedCache already registered upstream.
        services.AddDistributedMemoryCache();
        services.AddSingleton<IFhirAccessTokenCache, DistributedFhirAccessTokenCache>();

        // mTLS + connection pooling for outbound FHIR connections.
        services.Configure<MutualTlsOptions>(configuration.GetSection("Connectivity:Mtls"));
        services.Configure<FhirConnectionPoolOptions>(configuration.GetSection("Connectivity:ConnectionPool"));
        services.AddSingleton<MutualTlsCertificateProvider>();

        // Interactive OAuth tokens + in-flight sign-in state live in the shared distributed cache (Redis when configured,
        // in-process distributed-memory otherwise) so the callback can land on any node, a later pipeline run (API or
        // Worker) reads the token back, and both survive a restart. This is what makes the interactive application types
        // (standalone / EHR launch / patient) work beyond a single synchronous, single-instance run.
        services.AddSingleton<IFhirAuthorizationCodeTokenStore, DistributedFhirAuthorizationCodeTokenStore>();
        // Short-lived state of in-flight interactive OAuth sign-ins, between the authorize redirect and the callback.
        services.AddSingleton<IOAuthAuthorizationStateStore, DistributedOAuthAuthorizationStateStore>();

        services.AddHttpClient<SmartBackendServicesTokenProvider>().AddMutualTls();
        services.AddHttpClient<OAuth2ClientCredentialsTokenProvider>().AddMutualTls();
        // Interactive SMART (authorization-code + PKCE): the vendor-neutral provider backs the application-type axis
        // (EHR launch / standalone / patient); Healow + Epic interactive pin the provider name for their audit trails.
        services.AddHttpClient<SmartAuthorizationCodeTokenProvider>().AddMutualTls();
        services.AddHttpClient<HealowAuthorizationCodeTokenProvider>().AddMutualTls();
        services.AddHttpClient<EpicInteractiveTokenProvider>().AddMutualTls();
        services.AddHttpClient<MeditechGreenfieldTokenProvider>().AddMutualTls();
        services.AddHttpClient<EpicFhirSourceClient>().AddMutualTls();
        // Shares Epic's EpicFhirClientOptions and every base behaviour — it differs only in naming itself "FHIR"
        // rather than "Epic FHIR" in logs and errors (see GenericFhirSourceClient).
        services.AddHttpClient<GenericFhirSourceClient>().AddMutualTls();
        // Shares Epic's EpicFhirClientOptions (retry/timeout/throttle knobs) — no athenahealth-specific values are
        // called out in the integration spec, so the same IOptions<EpicFhirClientOptions> singleton applies here too.
        services.AddHttpClient<AthenahealthFhirSourceClient>().AddMutualTls();
        // Same rationale as athenahealth above — no eCW-specific retry/timeout values are confirmed yet.
        services.AddHttpClient<EClinicalWorksFhirSourceClient>().AddMutualTls();

        // Application-type axis: each ApplicationType maps to a strategy that owns its grant/launch flow, validation
        // and descriptor. Resolved from the registry (never a switch — enforced by ApplicationTypeDispatchTests).
        // Scoped, because the strategies depend on the HttpClient-backed token providers.
        services.AddScoped<ISourceApplicationStrategy, BackendServicesApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategy, EhrLaunchApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategy, StandaloneApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategy, PatientApplicationStrategy>();
        services.AddScoped<ISourceApplicationStrategyRegistry, SourceApplicationStrategyRegistry>();

        // Unauthenticated reachability check against a source's /metadata, used by the preflight decorator below to
        // tell "the vendor said no" apart from "the vendor said nothing". Its own client so the short probe timeout
        // and this call's failures stay isolated from the real FHIR/token clients' retry budgets.
        services.AddHttpClient<HttpSourceAvailabilityProbe>().AddMutualTls();
        services.AddScoped<ISourceAvailabilityProbe>(serviceProvider =>
            serviceProvider.GetRequiredService<HttpSourceAvailabilityProbe>());

        // Composite picks the grant per source: the application-type strategy (registry) when set, else legacy
        // inference — Healow (auth-code + PKCE), MEDITECH Greenfield (confidential JSON), SMART JWT (Epic), or OAuth2
        // client-credentials (Cerner/Allscripts/generic FHIR).
        services.AddScoped<CompositeFhirAccessTokenProvider>();

        // The registered IFhirAccessTokenProvider is the composite wrapped in the availability preflight, so every
        // application type gets outage-vs-credentials wording from one registration. The decorator re-implements
        // IFhirPatientContextProvider/IFhirGrantedScopeProvider as pass-throughs because callers feature-detect
        // this registration by pattern-matching on those — see its remarks.
        services.AddScoped<IFhirAccessTokenProvider>(serviceProvider =>
            new PreflightFhirAccessTokenProvider(
                // Resolved by CONCRETE type on purpose: the decorator's inner dependency is typed as
                // IFhirAccessTokenProvider (so it stays testable), and asking the container for that interface
                // here would resolve this very registration and recurse.
                serviceProvider.GetRequiredService<CompositeFhirAccessTokenProvider>(),
                serviceProvider.GetRequiredService<ISourceAvailabilityProbe>(),
                serviceProvider.GetService<ILogger<PreflightFhirAccessTokenProvider>>()));

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
        // Bulk export downloads potentially large NDJSON files — opt this client into transparent gzip so the transfer
        // is compressed when the server supports it. Decompression is handled by the handler before the NDJSON reader
        // sees the stream, so the parse/govern/map/write pipeline is unchanged.
        services.AddHttpClient<FhirRestBulkExportClient>().AddMutualTls(automaticDecompression: true);
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

    // Configures an HttpClient's primary handler to present the mTLS client certificate when enabled. Callers may opt
    // into transparent gzip decompression (default off, so no other client's behavior changes).
    private static IHttpClientBuilder AddMutualTls(this IHttpClientBuilder builder, bool automaticDecompression = false)
    {
        return builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            serviceProvider.GetRequiredService<MutualTlsCertificateProvider>().CreatePrimaryHandler(automaticDecompression));
    }
}
