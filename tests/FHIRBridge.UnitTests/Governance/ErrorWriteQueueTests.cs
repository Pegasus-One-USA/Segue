using FHIRBridge.Governance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorWriteQueueTests
{
    [Fact]
    public async Task Capturing_an_error_does_not_wait_for_a_blocked_table()
    {
        var gate = new TaskCompletionSource();
        var governance = new Mock<IGovernanceLogger>();
        governance.Setup(g => g.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

        var queue = new ErrorWriteQueue();
        var manager = new GlobalExceptionManager(
            governance.Object, new DefaultExceptionClassifier(), scrubber: new ErrorScrubber(), writeQueue: queue);

        // The table write below never completes (a lock), yet capture returns straight away with a reference id.
        var capture = manager.CaptureAsync(new InvalidOperationException("boom"), new ExceptionContext(Module: "Api"));
        var finished = await Task.WhenAny(capture, Task.Delay(2000));

        finished.Should().BeSameAs(capture);
        (await capture).ErrorReferenceId.Should().NotBeNull();
    }

    [Fact]
    public async Task The_background_writer_retries_a_locked_table_and_keeps_the_same_reference_id()
    {
        var calls = 0;
        var stored = new List<ErrorEntry>();
        var governance = new Mock<IGovernanceLogger>();
        governance.Setup(g => g.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Returns<ErrorEntry, CancellationToken>((e, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new TimeoutException("lock timeout");
                stored.Add(e);
                return Task.CompletedTask;
            });

        var options = Options.Create(new ErrorCaptureOptions());
        var services = new ServiceCollection();
        services.AddSingleton(governance.Object);
        services.AddSingleton(new ApplicationInsightsErrorSink(options));
        services.AddSingleton(options);
        services.AddScoped<IErrorSinkRouter, ErrorSinkRouter>();
        using var provider = services.BuildServiceProvider();

        var queue = new ErrorWriteQueue();
        var writer = new ErrorWriteService(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ErrorWriteService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        var manager = new GlobalExceptionManager(governance.Object, new DefaultExceptionClassifier(), scrubber: new ErrorScrubber(), writeQueue: queue);
        var report = await manager.CaptureAsync(new InvalidOperationException("boom"), new ExceptionContext(Module: "Api"));

        for (var i = 0; i < 100 && stored.Count == 0; i++) await Task.Delay(100);
        await writer.StopAsync(CancellationToken.None);

        stored.Should().ContainSingle().Which.ErrorReferenceId.Should().Be(report.ErrorReferenceId);
        stored[0].OccurredUtc.Should().NotBeNull();
    }
}
