using FHIRBridge.Governance;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorSinkRouterTests
{
    private static ErrorEntry Entry() => new("Error", "InvalidOperationException", "boom");

    private static ErrorSinkRouter Router(ErrorSinkMode mode, Mock<IGovernanceLogger> logger, string? connectionString = null) =>
        new(logger.Object,
            new ApplicationInsightsErrorSink(Options.Create(new ErrorCaptureOptions
            {
                Sinks = mode,
                ApplicationInsights = { ConnectionString = connectionString },
            })),
            Options.Create(new ErrorCaptureOptions
            {
                Sinks = mode,
                ApplicationInsights = { ConnectionString = connectionString },
            }));

    [Fact]
    public async Task Table_WritesToTable()
    {
        var logger = new Mock<IGovernanceLogger>();
        await Router(ErrorSinkMode.Table, logger).WriteAsync(Entry());
        logger.Verify(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ErrorSinkMode.ApplicationInsights)]
    [InlineData(ErrorSinkMode.Both)]
    public async Task AppInsightsRequestedButNoConnectionString_StillWritesToTable(ErrorSinkMode mode)
    {
        var logger = new Mock<IGovernanceLogger>();
        await Router(mode, logger).WriteAsync(Entry());
        logger.Verify(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AppInsightsOnly_SkipsTable()
    {
        var logger = new Mock<IGovernanceLogger>();
        var router = Router(ErrorSinkMode.ApplicationInsights, logger, "InstrumentationKey=00000000-0000-0000-0000-000000000000");
        await router.WriteAsync(Entry());
        logger.Verify(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TableFailure_Propagates_SoManagerCanRetry()
    {
        var logger = new Mock<IGovernanceLogger>();
        logger.Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var act = () => Router(ErrorSinkMode.Table, logger).WriteAsync(Entry());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Manager_WithRouter_ScrubsAndMarksCaptured()
    {
        ErrorEntry? written = null;
        var logger = new Mock<IGovernanceLogger>();
        logger.Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ErrorEntry, CancellationToken>((e, _) => written = e).Returns(Task.CompletedTask);
        var options = Options.Create(new ErrorCaptureOptions());
        var scrubber = new ErrorScrubber();
        var manager = new GlobalExceptionManager(
            logger.Object, new DefaultExceptionClassifier(), scrubber: scrubber,
            sinkRouter: new ErrorSinkRouter(logger.Object, new ApplicationInsightsErrorSink(options), options));

        var ex = new InvalidOperationException("failed for ssn 123-45-6789");
        var report = await manager.CaptureAsync(ex, new ExceptionContext("Test"));

        report.ErrorReferenceId.Should().NotBeNull();
        written!.Message.Should().NotContain("123-45-6789");
        CapturedExceptionRegistry.WasCaptured(ex).Should().BeTrue();
    }
}
