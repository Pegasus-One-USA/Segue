using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// The workflow list's "Writes to ..." badge: only an EHR Write-Back that writes into the EHR for real counts. A test
/// run (dest_testAsVendor set) and a dry run (dest_dryRun anything but false) never do.
/// </summary>
public sealed class LiveEhrWriteTargetsTests
{
    private static WorkflowDefinition NewWorkflow() => new(Guid.NewGuid(), "Allergies to the EHR", version: 1, isEnabled: true);

    private static void AddWriteBack(WorkflowDefinition workflow, string configurationJson, bool isEnabled = true) =>
        workflow.AddNode(
            WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, rank: 70,
            configurationJson: configurationJson, isEnabled: isEnabled);

    [Fact]
    public void A_live_write_back_names_its_EHR()
    {
        var workflow = NewWorkflow();
        AddWriteBack(workflow, """{"dest_ehrVendor":"Epic","dest_dryRun":"false"}""");

        LiveEhrWriteTargets.Of(workflow.Nodes).Should().Equal("Epic");
    }

    [Fact]
    public void Every_live_EHR_is_named_once()
    {
        var workflow = NewWorkflow();
        AddWriteBack(workflow, """{"dest_ehrVendor":"Epic","dest_dryRun":"false"}""");
        AddWriteBack(workflow, """{"config":{"dest_ehrVendor":"Athenahealth","dest_dryRun":"false"}}""");
        AddWriteBack(workflow, """{"dest_ehrVendor":"Epic","dest_dryRun":"false"}""");
        AddWriteBack(workflow, """{"dest_ehrVendor":"GenericFhir","dest_dryRun":"false"}""");

        LiveEhrWriteTargets.Of(workflow.Nodes).Should().Equal("Epic", "Athenahealth", "GenericFhir");
    }

    [Theory]
    [InlineData("""{"dest_ehrVendor":"Healow","dest_dryRun":"true"}""")]
    [InlineData("""{"dest_ehrVendor":"Healow"}""")]
    [InlineData("""{"dest_ehrVendor":"GenericFhir","dest_dryRun":"false","dest_testAsVendor":"Healow"}""")]
    [InlineData("""{"dest_dryRun":"false"}""")]
    [InlineData("{not json")]
    public void A_test_run_a_dry_run_or_an_unreadable_node_is_not_live(string configurationJson)
    {
        var workflow = NewWorkflow();
        AddWriteBack(workflow, configurationJson);

        LiveEhrWriteTargets.Of(workflow.Nodes).Should().BeEmpty();
    }

    [Fact]
    public void A_disabled_write_back_or_another_destination_is_not_live()
    {
        var workflow = NewWorkflow();
        AddWriteBack(workflow, """{"dest_ehrVendor":"Epic","dest_dryRun":"false"}""", isEnabled: false);
        workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 70,
            configurationJson: """{"dest_ehrVendor":"Epic","dest_dryRun":"false"}""");

        LiveEhrWriteTargets.Of(workflow.Nodes).Should().BeEmpty();
    }
}
