using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

[Collection("ErrorCaptureRelay")]
public sealed class SwallowedErrorTests
{
    [Fact]
    public void Report_PublishesToTheRelay_WithAreaCategory_AndPinsContextNames()
    {
        RelayedLogError? seen = null;
        ErrorCaptureRelay.Handler = e => seen = e;
        try
        {
            var failure = new InvalidOperationException("search failed");
            using (ErrorContext.Push(workflowName: "Epic sync", nodeName: "Extract"))
            {
                SwallowedError.Report(failure, "Source.PractitionerRole search");
            }

            seen.Should().NotBeNull();
            seen!.Category.Should().Be("Swallowed:Source.PractitionerRole search");
            seen.Exception.Should().BeSameAs(failure);
            ErrorContext.For(failure)!.WorkflowName.Should().Be("Epic sync");
            ErrorContext.For(failure)!.NodeName.Should().Be("Extract");
        }
        finally
        {
            ErrorCaptureRelay.Handler = null;
        }
    }

    [Fact]
    public void Report_WithNoHandler_DoesNothingAndDoesNotThrow()
    {
        ErrorCaptureRelay.Handler = null;
        var act = () => SwallowedError.Report(new Exception("x"), "Anywhere");
        act.Should().NotThrow();
    }
}
