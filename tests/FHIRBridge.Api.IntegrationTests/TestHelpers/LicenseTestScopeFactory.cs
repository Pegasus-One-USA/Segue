using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.IntegrationTests.TestHelpers;

/// <summary>
/// <c>InMemoryConfigurationRepository</c> — the no-DB dev/test fallback store — resolves the real license quota
/// guard through a fresh <see cref="IServiceScopeFactory"/> scope at its Add choke points (see
/// <c>LicenseEnforcementSaveChangesInterceptor</c>'s remarks for why the EF-backed stores' central interceptor
/// can't cover these). Tests that construct that store directly, bypassing the app's real DI container, need a
/// minimal scope factory to satisfy the dependency; this always-passes fake is correct for every test that
/// isn't itself exercising license enforcement.
///
/// Deliberately a near-duplicate of FHIRBridge.UnitTests' helper of the same name rather than a shared one:
/// that lives in a sibling TEST project, and a test project referencing another test project to borrow a
/// fixture is a worse dependency than these few lines. Registers only <see cref="ILicenseQuotaGuard"/> — the
/// one guard the stores used here actually resolve; the UnitTests copy also registers the pipeline-run guard
/// because it constructs <c>InMemoryWorkflowDefinitionStore</c>, which this project does not.
/// </summary>
public static class LicenseTestScopeFactory
{
    public static IServiceScopeFactory Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseQuotaGuard, NoOpLicenseQuotaGuard>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class NoOpLicenseQuotaGuard : ILicenseQuotaGuard
    {
        public Task EnsureUserQuotaAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType vendorType, string baseUrl, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType vendorType, string baseUrl, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureResourceTypeAllowedAsync(string resourceType, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureDestinationTypeAllowedAsync(DestinationType destinationType, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
