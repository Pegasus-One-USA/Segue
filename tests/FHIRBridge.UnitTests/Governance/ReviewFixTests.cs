using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Governance;
using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ReviewFixTests
{
    [Fact]
    public void TypographicQuotes_KeepNamesReadable_WhereAsciiQuotesAreMasked()
    {
        var scrubber = new ErrorScrubber();

        scrubber.ScrubText("Step 2/5 [Source] \u2018Extract Patients\u2019 (EpicSource) started.").Should().Contain("Extract Patients");
        scrubber.ScrubText("Step 2/5 [Source] 'Extract Patients' (EpicSource) started.").Should().NotContain("Extract Patients");
    }

    [Fact]
    public void Timeline_KeepsTheEndOfTheTrace_AndTheErrors_WhenOverTheCap()
    {
        var start = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        var errors = Enumerable.Range(0, 300)
            .Select(i => new ErrorLogDto(Guid.NewGuid(), start.AddSeconds(i), "WorkflowDebug", "WorkflowDebug", $"trace line {i}", null, "Workflow", "corr"))
            .Append(new ErrorLogDto(Guid.NewGuid(), start.AddSeconds(301), "Error", "SqlException", "the real failure", null, "Workflow", "corr", "ERR-1"))
            .ToList();
        var result = new CorrelationSearchResultDto("corr", null, [], [], [], [], [], [], [], errors, [], [], [], [], [], [], []);

        var timeline = ErrorCorrelationBuilder.Build(result, new ErrorScrubber(), includeSecurityLogs: false);

        timeline.Events.Count.Should().BeLessThanOrEqualTo(ErrorCorrelationBuilder.MaxEvents);
        timeline.Truncated.Should().BeTrue();
        timeline.Events.Should().Contain(e => e.Title.Contains("trace line 299"), "the last step before the failure must survive");
        timeline.Events.Should().Contain(e => e.Title == "SqlException", "the error row itself must survive");
        timeline.Events.Should().Contain(e => e.Title.Contains("trace line 0"), "how the run began is kept too");
    }

    [Fact]
    public async Task TraceEntry_IsRetriedWithAFreshReferenceId_WhenTheFirstWriteCollides()
    {
        var attempts = 0;
        var logger = new Mock<IGovernanceLogger>();
        logger.Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++attempts == 1 ? Task.FromException(new InvalidOperationException("duplicate reference id")) : Task.CompletedTask);
        var policy = new Mock<IErrorCapturePolicy>();
        policy.SetupGet(p => p.Current).Returns(new ErrorLogSettings { CaptureSeverities = ["WorkflowDebug"] });
        var manager = new GlobalExceptionManager(logger.Object, new DefaultExceptionClassifier(), policy: policy.Object);

        var id = await manager.CaptureTraceAsync("WorkflowDebug", "WorkflowDebug", "Step 1/1 started", new ExceptionContext("Workflow"));

        id.Should().NotBeNull();
        attempts.Should().Be(2);
    }
}

public sealed class ResourceIdScrubbingTests
{
    private readonly ErrorScrubber _scrubber = new();

    [Fact]
    public void FhirResourceIds_BecomeStableTokens_SoNoIdReachesLogsOrExports()
    {
        var a = _scrubber.ScrubText("Resource Patient/eXyz123abc failed; also see Observation/e-9.7 and Patient/eXyz123abc again.");

        a.Should().NotContain("eXyz123abc").And.NotContain("e-9.7");
        a.Should().MatchRegex(@"Patient/#[0-9a-f]{8}");
        a.Should().MatchRegex(@"Observation/#[0-9a-f]{8}");

        var tokens = System.Text.RegularExpressions.Regex.Matches(a, @"Patient/#[0-9a-f]{8}").Select(m => m.Value).ToList();
        tokens.Should().HaveCount(2);
        tokens[0].Should().Be(tokens[1], "the same id maps to the same token, so lines about one resource still line up");
    }

    [Fact]
    public void OrdinaryPathsAndWords_AreLeftAlone()
    {
        _scrubber.ScrubText("POST application/json to /fhir/R4 and read/write access, I/O error").Should()
            .Contain("application/json").And.Contain("read/write").And.Contain("I/O");
    }

    [Fact]
    public void StackTraces_AlsoHaveResourceIdsTokenised()
    {
        _scrubber.ScrubStackTrace("at X.Y() Patient/abc123XYZ failed").Should().NotContain("abc123XYZ");
    }
}
