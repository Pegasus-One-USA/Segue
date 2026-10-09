using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorLogSettingsTests
{
    [Fact]
    public void Defaults_CaptureErrorsAndCritical_NotWarningsOrInformation()
    {
        var s = ErrorLogSettings.Default;
        s.ShouldCapture("Error", "Network").Should().BeTrue();
        s.ShouldCapture("Critical", null).Should().BeTrue();
        s.ShouldCapture("Warning", null).Should().BeFalse();
        s.ShouldCapture("Information", null).Should().BeFalse();
        s.AutoClearEnabled.Should().BeFalse();
    }

    [Fact]
    public void RoutineInformationalOutcomes_AreNotGovernedBySettings()
    {
        new ErrorLogSettings { CaptureSeverities = ["Critical"] }.ShouldCapture("Informational", null).Should().BeTrue();
    }

    [Fact]
    public void Normalize_ClampsRetention_AndDropsUnknownValues()
    {
        var s = new ErrorLogSettings
        {
            CaptureSeverities = ["error", "Bogus", "ERROR"],
            CaptureCategories = ["network", "nope"],
            RetentionDays = 100000,
        }.Normalize();

        s.CaptureSeverities.Should().Equal("Error");
        s.CaptureCategories.Should().Equal("Network");
        s.RetentionDays.Should().Be(ErrorLogSettings.MaxRetentionDays);
        (s with { RetentionDays = 0 }).Normalize().RetentionDays.Should().Be(1);
    }

    [Fact]
    public void CategoryFilter_SkipsDisabledCategories()
    {
        var s = new ErrorLogSettings { CaptureCategories = ["Database"] };
        s.ShouldCapture("Error", "Database").Should().BeTrue();
        s.ShouldCapture("Error", "Validation").Should().BeFalse();
    }

    [Fact]
    public async Task Manager_DoesNotRecordAnError_WhenItsSeverityIsDisabled()
    {
        var logger = new Mock<IGovernanceLogger>();
        var policy = new Mock<IErrorCapturePolicy>();
        policy.SetupGet(p => p.Current).Returns(new ErrorLogSettings { CaptureSeverities = ["Critical"] });
        var manager = new GlobalExceptionManager(logger.Object, new DefaultExceptionClassifier(), policy: policy.Object);

        var report = await manager.CaptureAsync(new InvalidOperationException("x"), new ExceptionContext("Test", Severity: "Error"));

        report.ErrorReferenceId.Should().BeNull();
        logger.Verify(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Manager_RecordsWarningEntries_WhenEnabled()
    {
        ErrorEntry? written = null;
        var logger = new Mock<IGovernanceLogger>();
        logger.Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ErrorEntry, CancellationToken>((e, _) => written = e).Returns(Task.CompletedTask);
        var policy = new Mock<IErrorCapturePolicy>();
        policy.SetupGet(p => p.Current).Returns(new ErrorLogSettings { CaptureSeverities = ["Error", "Warning"] });
        var manager = new GlobalExceptionManager(logger.Object, new DefaultExceptionClassifier(), policy: policy.Object);

        await manager.CaptureAsync(new InvalidOperationException("careful"), new ExceptionContext("Test", Severity: "Warning"));

        written.Should().NotBeNull();
        written!.Severity.Should().Be("Warning");
    }
}
