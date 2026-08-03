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
    public async Task CaptureExpectedAsync_SwallowsGovernanceLoggerFailures()
    {
        var governanceLogger = new Mock<IGovernanceLogger>();
        governanceLogger
            .Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unreachable"));

        var manager = new GlobalExceptionManager(governanceLogger.Object, new DefaultExceptionClassifier());

        var referenceId = await manager.CaptureExpectedAsync(
            new ExpectedFailure("InvalidOperationException", "Wrong password."),
            new ExceptionContext(Module: "Api"));

        referenceId.Should().NotBeNullOrWhiteSpace();
    }
}
