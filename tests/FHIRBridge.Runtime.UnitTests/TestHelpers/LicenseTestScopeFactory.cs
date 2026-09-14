using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.UnitTests.TestHelpers;

/// <summary>
/// <c>InMemoryWorkflowDefinitionStore</c> resolves the Runtime-plane license guard bridge through a fresh
/// <see cref="IServiceScopeFactory"/> scope at its Save choke point (it's a singleton; the real guard is
/// scoped). Tests that construct it directly (bypassing the app's real DI container) need a minimal scope
/// factory to satisfy that constructor dependency; this always-passes fake is correct for every test here,
/// none of which exercise license enforcement.
/// </summary>
public static class LicenseTestScopeFactory
{
    public static IServiceScopeFactory Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPipelineRunLicenseGuard, NoOpPipelineRunLicenseGuard>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class NoOpPipelineRunLicenseGuard : IPipelineRunLicenseGuard
    {
        public Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
