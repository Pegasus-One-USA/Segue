using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorLogSettingsRoundTripTests
{
    [Fact]
    public async Task OnlyWorkflowDebugSelected_SurvivesSaveAndReload_AndTheLivePolicySeesIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISystemSettingRepository, InMemorySystemSettingRepository>();
        services.AddScoped<IErrorLogSettingsStore, SystemSettingsErrorLogSettingsStore>();
        services.AddSingleton<IErrorCapturePolicy, SettingsErrorCapturePolicy>();
        await using var provider = services.BuildServiceProvider();

        // Exactly what the settings screen sends when only "WorkflowDebug" is ticked.
        var request = new ErrorLogSettings
        {
            CaptureSeverities = ["WorkflowDebug"],
            CaptureCategories = ErrorLogSettings.AllCategories,
            AutoClearEnabled = false,
            RetentionDays = 90,
            WorkflowDebugDetail = "Stages",
        };

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IErrorLogSettingsStore>().SaveAsync(request.Normalize(), CancellationToken.None);
        }

        var policy = provider.GetRequiredService<IErrorCapturePolicy>();
        await policy.RefreshAsync();

        policy.Current.CaptureSeverities.Should().Equal("WorkflowDebug");
        policy.Current.CapturesWorkflowDebug.Should().BeTrue();
        policy.Current.WorkflowDebugLevel.Should().Be(2);
        policy.Current.ShouldCapture("WorkflowDebug", null).Should().BeTrue();
        policy.Current.ShouldCapture("Error", "Network").Should().BeFalse();
    }
}
