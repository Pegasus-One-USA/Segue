using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.UnitTests.TestHelpers;

/// <summary>
/// <see cref="InMemoryConfigurationRepository"/>/<see cref="InMemoryUserAccessRepository"/> (Infrastructure) and
/// <see cref="InMemoryWorkflowDefinitionStore"/> (Runtime.Application) — the no-DB dev/test fallback stores —
/// resolve the real license quota guard through a fresh <see cref="IServiceScopeFactory"/> scope at their
/// Add/Save choke points (see <c>LicenseEnforcementSaveChangesInterceptor</c>'s remarks for why the EF-backed
/// stores' central interceptor can't cover these). Tests that construct these stores directly (bypassing the
/// app's real DI container) need a minimal scope factory to satisfy that constructor dependency; this always-
/// passes fake is correct for every test that isn't itself exercising license enforcement.
/// </summary>
public static class LicenseTestScopeFactory
{
    public static IServiceScopeFactory Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseQuotaGuard, NoOpLicenseQuotaGuard>();
        services.AddSingleton<IPipelineRunLicenseGuard, NoOpPipelineRunLicenseGuard>();
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

    private sealed class NoOpPipelineRunLicenseGuard : IPipelineRunLicenseGuard
    {
        public Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
