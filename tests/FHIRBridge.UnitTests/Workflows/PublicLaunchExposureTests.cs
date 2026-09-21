using FHIRBridge.Api.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// Pins the public-launch default on each layer that can set it. The layers deliberately DISAGREE, and that is
/// the point of these tests — the difference is a product decision that is easy to "tidy up" by accident:
///
/// <list type="bullet">
/// <item><see cref="WorkflowDefinitionRequest"/> defaults to <c>true</c>: a workflow created through the API is
/// expected to be launchable by the third-party app it was built for straight away, with no extra admin step.</item>
/// <item><see cref="WorkflowDefinition"/> (and the database column) default to <c>false</c>: a workflow created
/// through any other path is not silently exposed.</item>
/// </list>
///
/// What actually bounds the exposure is not this flag on its own but the allowed-origins check the anonymous
/// endpoints apply (CallerIdOriginValidator) — so a publicly launchable workflow is still only reachable from an
/// origin an admin has registered.
/// </summary>
public sealed class PublicLaunchExposureTests
{
    [Fact]
    public void An_api_create_request_is_publicly_launchable_by_default()
    {
        // Exactly what the portal's "New workflow" modal sends — name, enabled, empty graph, nothing else.
        var request = new WorkflowDefinitionRequest("Epic to Warehouse", IsEnabled: true, Nodes: [], Edges: []);

        request.IsPubliclyLaunchable.Should().BeTrue(
            "a workflow created through the API is meant to be launchable by its third-party app without a second step");
    }

    [Fact]
    public void The_domain_entity_is_not_publicly_launchable_by_default()
    {
        // Intentionally the opposite of the request model above: only the API create path grants this.
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "Epic to Warehouse", version: 1);

        workflow.IsPubliclyLaunchable.Should().BeFalse(
            "a workflow built outside the API create path must not become publicly launchable implicitly");
    }

    [Fact]
    public void Public_launch_can_be_set_explicitly_either_way()
    {
        // The explicit opt-out must work — this is the row menu's "Revoke public launch".
        var privateRequest = new WorkflowDefinitionRequest(
            "Private", IsEnabled: true, Nodes: [], Edges: [], IsPubliclyLaunchable: false);
        var publicWorkflow = new WorkflowDefinition(
            Guid.NewGuid(), "Public", version: 1, isEnabled: true, isPubliclyLaunchable: true);

        privateRequest.IsPubliclyLaunchable.Should().BeFalse();
        publicWorkflow.IsPubliclyLaunchable.Should().BeTrue();
    }
}
