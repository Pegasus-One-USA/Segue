using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

[Collection("ErrorCaptureRelay")]
public sealed class WorkflowDebugTests
{
    [Fact]
    public void Write_DoesNothing_WhenDisabled()
    {
        var calls = 0;
        ErrorCaptureRelay.Handler = _ => calls++;
        ErrorCaptureRelay.WorkflowDebugEnabled = false;
        try
        {
            WorkflowDebug.Write("Step 1/3 started");
            calls.Should().Be(0);
        }
        finally
        {
            ErrorCaptureRelay.Handler = null;
        }
    }

    [Fact]
    public void Write_PublishesStepLine_WithWorkflowIdsAndStepPosition()
    {
        RelayedLogError? seen = null;
        ErrorCaptureRelay.Handler = e => seen = e;
        ErrorCaptureRelay.WorkflowDebugEnabled = true;
        try
        {
            using (ErrorContext.Push(workflowName: "Epic to SQL", workflowId: "wf-1", executionId: "run-9", correlationId: "corr-3", totalSteps: 5))
            using (ErrorContext.Push(nodeName: "Extract", nodeType: "EpicSource", stepNumber: 2, stage: "Source"))
            {
                WorkflowDebug.Write("Step 2/5 [Source] 'Extract' started.");
            }

            seen.Should().NotBeNull();
            seen!.Level.Should().Be("WorkflowDebug");
            seen.Properties!["WorkflowName"].Should().Be("Epic to SQL");
            seen.Properties["WorkflowId"].Should().Be("wf-1");
            seen.Properties["ExecutionId"].Should().Be("run-9");
            seen.Properties["StepNumber"].Should().Be("2");
            seen.Properties["TotalSteps"].Should().Be("5");
            seen.Properties["Stage"].Should().Be("Source");
        }
        finally
        {
            ErrorCaptureRelay.Handler = null;
            ErrorCaptureRelay.WorkflowDebugEnabled = false;
        }
    }

    [Fact]
    public async Task Manager_RecordsTraceEntries_OnlyWhenWorkflowDebugIsEnabled()
    {
        ErrorEntry? written = null;
        var logger = new Mock<IGovernanceLogger>();
        logger.Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ErrorEntry, CancellationToken>((e, _) => written = e).Returns(Task.CompletedTask);
        var policy = new Mock<IErrorCapturePolicy>();
        var manager = new GlobalExceptionManager(logger.Object, new DefaultExceptionClassifier(), policy: policy.Object);
        var context = new ExceptionContext("Workflow", WorkflowName: "Wf", ExecutionId: "run-1", WorkflowId: "wf-1");

        policy.SetupGet(p => p.Current).Returns(ErrorLogSettings.Default);
        (await manager.CaptureTraceAsync("WorkflowDebug", "WorkflowDebug", "Step 1/2 started", context)).Should().BeNull();
        written.Should().BeNull();

        policy.SetupGet(p => p.Current).Returns(new ErrorLogSettings { CaptureSeverities = ["Error", "WorkflowDebug"] });
        var when = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        (await manager.CaptureTraceAsync("WorkflowDebug", "WorkflowDebug", "Step 1/2 started", context, when)).Should().NotBeNull();
        written!.Severity.Should().Be("WorkflowDebug");
        written.ExecutionId.Should().Be("run-1");
        written.WorkflowName.Should().Be("Wf");
        written.OccurredUtc.Should().Be(when);
    }
}

[Collection("ErrorCaptureRelay")]
public sealed class WorkflowDebugDetailTests
{
    private static List<string> Capture(int level, Action act)
    {
        var lines = new List<string>();
        ErrorCaptureRelay.Handler = e => lines.Add(e.Message);
        ErrorCaptureRelay.WorkflowDebugEnabled = true;
        ErrorCaptureRelay.WorkflowDebugLevel = level;
        try
        {
            act();
        }
        finally
        {
            ErrorCaptureRelay.Handler = null;
            ErrorCaptureRelay.WorkflowDebugEnabled = false;
            ErrorCaptureRelay.WorkflowDebugLevel = 1;
        }

        return lines;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void Stage_AndResource_LinesAppearOnlyAtTheirDetailLevel(int level, int expected)
    {
        var lines = Capture(level, () =>
        {
            using var scope = ErrorContext.Push(workflowName: "W", executionId: "run-1");
            WorkflowDebug.Stage("stage line");
            WorkflowDebug.Resource("resource line");
        });

        lines.Count.Should().Be(expected);
    }

    [Fact]
    public void Resource_LinesAreCappedPerRun_ButFailuresHaveTheirOwnAllowance()
    {
        var lines = Capture(3, () =>
        {
            using var scope = ErrorContext.Push(workflowName: "W", executionId: "run-2");
            for (var i = 0; i < WorkflowDebug.MaxResourceLinesPerRun + 50; i++) WorkflowDebug.Resource($"ok {i}");
            WorkflowDebug.Resource("failed resource", isFailure: true);
        });

        lines.Count(l => l.StartsWith("ok")).Should().Be(WorkflowDebug.MaxResourceLinesPerRun);
        lines.Should().Contain("failed resource");
    }

    [Fact]
    public void Settings_NormalizeDetail_AndExposeItsLevel()
    {
        new ErrorLogSettings { WorkflowDebugDetail = "resources" }.Normalize().WorkflowDebugLevel.Should().Be(3);
        new ErrorLogSettings { WorkflowDebugDetail = "nonsense" }.Normalize().WorkflowDebugDetail.Should().Be("Steps");
        ErrorLogSettings.Default.WorkflowDebugLevel.Should().Be(1);
    }
}
