using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class GlobalExceptionManagerCaptureExpectedTests
{
    [Fact]
    public async Task CaptureExpectedAsync_LogsInformationalSeverityWithNoStackTrace_AndReturnsReferenceId()
    {
        ErrorEntry? captured = null;
        var governanceLogger = new Mock<IGovernanceLogger>();
        governanceLogger
            .Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ErrorEntry, CancellationToken>((entry, _) => captured = entry)
            .Returns(Task.CompletedTask);

        var manager = new GlobalExceptionManager(governanceLogger.Object, new DefaultExceptionClassifier());

        var referenceId = await manager.CaptureExpectedAsync(
            new ExpectedFailure("InvalidOperationException", "Duplicate mapping name."),
            new ExceptionContext(Module: "Api", CorrelationId: "corr-123"));

        referenceId.Should().NotBeNullOrWhiteSpace();
        captured.Should().NotBeNull();
        captured!.Severity.Should().Be("Informational");
        captured.StackTrace.Should().BeNull();
        captured.Category.Should().BeNull();
        captured.CorrelationId.Should().Be("corr-123");
        captured.ErrorReferenceId.Should().Be(referenceId);
    }

    [Fact]
    public async Task CaptureExpectedAsync_SwallowsGovernanceLoggerFailures_AndReturnsNullReferenceId()
    {
        var governanceLogger = new Mock<IGovernanceLogger>();
        governanceLogger
            .Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unreachable"));

        var manager = new GlobalExceptionManager(governanceLogger.Object, new DefaultExceptionClassifier());

        // The call must not throw even though nothing could actually be persisted — but since nothing was
        // persisted, there is no ErrorLogs row to point at, so the returned reference id must be null rather
        // than an orphaned id a caller might otherwise surface as a dead link.
        var referenceId = await manager.CaptureExpectedAsync(
            new ExpectedFailure("InvalidOperationException", "Wrong password."),
            new ExceptionContext(Module: "Api"));

        referenceId.Should().BeNull();
    }
}
