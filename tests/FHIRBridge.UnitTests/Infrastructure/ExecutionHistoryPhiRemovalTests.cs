using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Guards the PHI removal from Execution History. These tables used to persist whole Epic FHIR resources
/// (WorkflowNodeRunPayload.PayloadJson) and individual patient field values (FieldLineageEntry's
/// Source/DestinationValueJson), encrypted at rest — but encrypted PHI is still PHI: it is retained,
/// decryptable by the application, and in scope for breach notification.
///
/// The tests below are deliberately written against the TYPE SHAPE rather than behaviour: the guarantee
/// being protected is that there is nowhere on these records to put resource content in the first place.
/// If someone re-adds such a property, these fail immediately rather than the leak reappearing silently.
/// </summary>
public sealed class ExecutionHistoryPhiRemovalTests
{
    private static readonly string[] PhiBearingNames =
    {
        "PayloadJson", "SourceValueJson", "DestinationValueJson",
        "FetchedJson", "NormalizedJson", "MappedValuesJson",
    };

    [Fact]
    public void WorkflowNodeRunPayload_exposes_no_property_that_could_hold_resource_content()
    {
        var propertyNames = typeof(WorkflowNodeRunPayload).GetProperties().Select(p => p.Name);

        propertyNames.Should().NotIntersectWith(PhiBearingNames);
    }

    [Fact]
    public void FieldLineageEntry_exposes_no_property_that_could_hold_a_field_value()
    {
        var propertyNames = typeof(FieldLineageEntry).GetProperties().Select(p => p.Name);

        propertyNames.Should().NotIntersectWith(PhiBearingNames);
    }

    [Fact]
    public void WorkflowNodeRunPayload_still_carries_the_per_node_counts_the_history_screen_needs()
    {
        var propertyNames = typeof(WorkflowNodeRunPayload).GetProperties().Select(p => p.Name).ToArray();

        // Removing the PHI must not cost the "how many transformed / how much written" detail — that is
        // exactly what these columns are for, and what Execution History renders per node.
        propertyNames.Should().Contain("ItemCount");
        propertyNames.Should().Contain("ResourceTypeCountsJson");
        propertyNames.Should().Contain("DeliveryDetailJson");
    }

    [Fact]
    public void FieldLineageEntry_still_describes_the_transformation_itself()
    {
        var propertyNames = typeof(FieldLineageEntry).GetProperties().Select(p => p.Name).ToArray();

        // The lineage story — which field, through which node, with what config, and did it work — is all
        // metadata about the mapping rather than the patient, so it stays.
        propertyNames.Should().Contain("SourceField");
        propertyNames.Should().Contain("DestinationField");
        propertyNames.Should().Contain("NodeType");
        propertyNames.Should().Contain("ConfigJson");
        propertyNames.Should().Contain("Success");
    }
}
