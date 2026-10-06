using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

[Collection("ErrorCaptureRelay")]
public sealed class ResourceIdEndToEndTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
    }

    [Fact]
    public async Task ResourceLevelTraceLine_NeverCarriesTheRealId_ToTheStoredEntryOrTheApplicationLog()
    {
        var stored = new List<ErrorEntry>();
        var governance = new Mock<IGovernanceLogger>();
        governance.Setup(g => g.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ErrorEntry, CancellationToken>((e, _) => stored.Add(e)).Returns(Task.CompletedTask);

        var settings = new ErrorLogSettings { CaptureSeverities = ["Error", "WorkflowDebug"], WorkflowDebugDetail = "Resources" };
        var policy = new Mock<IErrorCapturePolicy>();
        policy.SetupGet(p => p.Current).Returns(settings);
        policy.Setup(p => p.RefreshAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var scrubber = new ErrorScrubber();
        var manager = new GlobalExceptionManager(governance.Object, new DefaultExceptionClassifier(), scrubber: scrubber, policy: policy.Object);
        var services = new ServiceCollection();
        services.AddScoped<IGlobalExceptionManager>(_ => manager);
        using var provider = services.BuildServiceProvider();

        var logger = new ListLogger<AmbientErrorCaptureService>();
        var options = Options.Create(new ErrorCaptureOptions());
        using var service = new AmbientErrorCaptureService(
            provider.GetRequiredService<IServiceScopeFactory>(), new ApplicationInsightsErrorSink(options), options, logger, policy.Object, scrubber);

        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 100 && !WorkflowDebug.ResourcesEnabled; i++) await Task.Delay(100);
            WorkflowDebug.ResourcesEnabled.Should().BeTrue();

            using (ErrorContext.Push(workflowName: "Epic sync", workflowId: "wf-1", executionId: "run-1", correlationId: "corr-1"))
            {
                WorkflowDebug.Resource("Resource Patient/eXyz123 fetched from source Epic Prod; record error: Observation/abc-77.9 not written.");
            }

            for (var i = 0; i < 100 && stored.Count == 0; i++) await Task.Delay(100);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        stored.Should().ContainSingle();
        stored[0].Message.Should().NotContain("eXyz123").And.NotContain("abc-77.9").And.MatchRegex(@"Patient/#[0-9a-f]{8}");
        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().OnlyContain(line => !line.Contains("eXyz123") && !line.Contains("abc-77.9"));
    }
}
