using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Infrastructure;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Auth;

/// <summary>
/// Composition-root guard for the availability preflight.
/// <para>
/// The decorator's inner dependency is typed as <see cref="IFhirAccessTokenProvider"/> — the same interface the
/// decorator itself is registered as — so the registration MUST resolve the inner instance by concrete type. If
/// someone later "simplifies" that to a plain interface resolution, the container recurses and every workflow run
/// dies at token acquisition with a stack overflow rather than anything diagnosable. Resolving it here is the
/// cheapest possible way to notice.
/// </para>
/// </summary>
public sealed class RuntimeAuthCompositionTests
{
    private static ServiceProvider BuildRuntimeServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddDataProtection()
            .Services;

        // AddRuntimeInfrastructure is not self-sufficient: CompositeFhirAccessTokenProvider needs an
        // IGovernanceLogger, which the composing host supplies via AddInfrastructure — a project this test
        // assembly cannot reference without inverting the layering. Stubbed rather than worked around, so the rest
        // of the runtime auth graph is still wired exactly as production wires it.
        services.AddSingleton(new Mock<IGovernanceLogger>().Object);

        return services
            .AddRuntimeInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void The_registered_token_provider_resolves_without_recursing()
    {
        using var provider = BuildRuntimeServices();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IFhirAccessTokenProvider>();

        resolved.Should().BeOfType<PreflightFhirAccessTokenProvider>();
    }

    /// <summary>
    /// Callers feature-detect the registered provider by pattern-matching these two interfaces
    /// (SourceConnectionRuntimeResolver for an interactive launch's resolved base URL, FhirSourceConnectorBase for
    /// patient context, SourceNodeExecutors for granted scopes). Losing them here would silently break interactive
    /// launches with no compile error and no test failure anywhere near the cause.
    /// </summary>
    [Fact]
    public void The_registered_token_provider_still_exposes_patient_context_and_granted_scopes()
    {
        using var provider = BuildRuntimeServices();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IFhirAccessTokenProvider>();

        resolved.Should().BeAssignableTo<IFhirPatientContextProvider>();
        resolved.Should().BeAssignableTo<IFhirGrantedScopeProvider>();
    }

    [Fact]
    public void The_availability_probe_is_resolvable()
    {
        using var provider = BuildRuntimeServices();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISourceAvailabilityProbe>()
            .Should().BeOfType<HttpSourceAvailabilityProbe>();
    }
}
