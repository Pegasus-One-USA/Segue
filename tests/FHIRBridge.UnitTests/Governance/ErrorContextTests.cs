using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorContextTests
{
    private static (GlobalExceptionManager Manager, Func<ErrorEntry?> Written) Create()
    {
        ErrorEntry? written = null;
        var logger = new Mock<IGovernanceLogger>();
        logger.Setup(x => x.LogErrorAsync(It.IsAny<ErrorEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ErrorEntry, CancellationToken>((e, _) => written = e).Returns(Task.CompletedTask);
        return (new GlobalExceptionManager(logger.Object, new DefaultExceptionClassifier()), () => written);
    }

    [Fact]
    public async Task Capture_AttachesNamesFromTheCurrentScope()
    {
        var (manager, written) = Create();

        using (ErrorContext.Push(workflowName: "Epic to SQL", nodeName: "Extract", nodeType: "EpicSource"))
        {
            ErrorContext.Set(sourceName: "Epic Prod", resourceType: "Patient");
            await manager.CaptureAsync(new InvalidOperationException("boom"), new ExceptionContext("Workflow"));
        }

        var entry = written()!;
        entry.WorkflowName.Should().Be("Epic to SQL");
        entry.NodeName.Should().Be("Extract");
        entry.NodeType.Should().Be("EpicSource");
        entry.SourceName.Should().Be("Epic Prod");
        entry.ResourceType.Should().Be("Patient");
        entry.DestinationName.Should().BeNull();
    }

    [Fact]
    public async Task Capture_UsesNamesPinnedToTheExceptionAfterTheNodeScopeEnded()
    {
        var (manager, written) = Create();
        var failure = new InvalidOperationException("node failed");

        using (ErrorContext.Push(workflowName: "Run"))
        {
            using (ErrorContext.Push(nodeName: "Write rows", nodeType: "SqlDestination"))
            {
                ErrorContext.Set(destinationName: "Warehouse");
                ErrorContext.Remember(failure);
            }

            await manager.CaptureAsync(failure, new ExceptionContext("Workflow"));
        }

        var entry = written()!;
        entry.WorkflowName.Should().Be("Run");
        entry.NodeName.Should().Be("Write rows");
        entry.DestinationName.Should().Be("Warehouse");
    }

    [Fact]
    public async Task Capture_ExplicitContextWinsOverAmbient_AndNamesAreScrubbed()
    {
        var (manager, written) = Create();

        using (ErrorContext.Push(workflowName: "Ambient"))
        {
            await manager.CaptureAsync(
                new InvalidOperationException("x"),
                new ExceptionContext("Workflow", WorkflowName: "Explicit mail jane@example.com"));
        }

        written()!.WorkflowName.Should().Be("Explicit mail [email]");
    }
}
