using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

[Collection("ErrorCaptureRelay")]
public sealed class AmbientWorkflowDebugEndToEndTests
{
    [Fact]
    public async Task EnabledWorkflowDebug_TraceLine_ReachesTheExceptionManager_WithItsContext()
    {
        var manager = new Mock<IGlobalExceptionManager>();
        string? seenMessage = null;
        ExceptionContext? seenContext = null;
        manager.Setup(m => m.CaptureTraceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ExceptionContext>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, ExceptionContext, DateTime?, CancellationToken>((_, _, message, context, _, _) =>
            {
                seenMessage = message;
                seenContext = context;
            })
            .ReturnsAsync("ERR-1");

        var services = new ServiceCollection();
        services.AddScoped(_ => manager.Object);
        using var provider = services.BuildServiceProvider();

        var policy = new Mock<IErrorCapturePolicy>();
        policy.SetupGet(p => p.Current).Returns(new ErrorLogSettings { CaptureSeverities = ["Error", "WorkflowDebug"] });
        policy.Setup(p => p.RefreshAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var options = Options.Create(new ErrorCaptureOptions());
        using var service = new AmbientErrorCaptureService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationInsightsErrorSink(options),
            options,
            NullLogger<AmbientErrorCaptureService>.Instance,
            policy.Object);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => ErrorCaptureRelay.WorkflowDebugEnabled);
            ErrorCaptureRelay.WorkflowDebugEnabled.Should().BeTrue("the settings enable WorkflowDebug");

            using (ErrorContext.Push(workflowName: "Epic sync", workflowId: "wf-1", executionId: "run-1", correlationId: "corr-1", totalSteps: 3))
            using (ErrorContext.Push(nodeName: "Extract", nodeType: "EpicSource", stepNumber: 1, stage: "Source"))
            {
                WorkflowDebug.Write("Step 1/3 [Source] 'Extract' started.");
            }

            await WaitUntil(() => seenMessage is not null);
            seenMessage.Should().Be("Step 1/3 [Source] 'Extract' started.");
            seenContext!.WorkflowName.Should().Be("Epic sync");
            seenContext.ExecutionId.Should().Be("run-1");
            seenContext.NodeName.Should().Be("Extract");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
