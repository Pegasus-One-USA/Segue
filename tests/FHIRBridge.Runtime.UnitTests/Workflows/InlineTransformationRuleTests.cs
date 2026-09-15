using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Phase 6 (plan §3.4): transformation rules carried by the node itself. This is what removes the
/// ResourcePipelineRouteId class of bug at its root — a node's rules travel with the node, so there is no route
/// id to be null, no save-time sweep that has to guess which orphans belong to the workflow being saved, and no
/// way for one workflow to pick up another's rules.
/// </summary>
public sealed class InlineTransformationRuleTests
{
    private static TransformationRule Rule(
        string sourceField,
        TransformNodeType nodeType = TransformNodeType.DateTimeFormat,
        string? resourceType = "Patient",
        int order = 0) =>
        new(
            TransformScope.ResourceType,
            nodeType,
            "{}",
            resourceType: resourceType,
            sourceField: sourceField,
            order: order,
            executionPhase: TransformExecutionPhase.FhirResource);

    [Fact]
    public async Task Source_fields_come_from_the_nodes_own_rules()
    {
        var resolver = new InlineFhirResourceRuleResolver(
            [Rule("Patient.birthDate"), Rule("Patient.name.family")]);

        var fields = await resolver.ResolveSourceFieldsAsync(
            "Patient", DestinationType.SqlServer, null, null, CancellationToken.None);

        fields.Should().BeEquivalentTo(["Patient.birthDate", "Patient.name.family"]);
    }

    /// <summary>The route id is the thing this design removes, so passing a null one — the exact state that made
    /// a rule inert under the old resolver — must change nothing at all here.</summary>
    [Fact]
    public async Task A_null_route_id_does_not_make_rules_inert()
    {
        var resolver = new InlineFhirResourceRuleResolver([Rule("Patient.birthDate")]);

        var rules = await resolver.ResolveAsync(
            "Patient", "Patient.birthDate", DestinationType.SqlServer,
            resourcePipelineRouteId: null, sourceSystem: null, CancellationToken.None);

        rules.Should().ContainSingle();
    }

    /// <summary>A rule belongs to exactly one node, so nothing about the destination can filter it out.</summary>
    [Fact]
    public async Task The_destination_type_does_not_filter_a_nodes_own_rules()
    {
        var resolver = new InlineFhirResourceRuleResolver([Rule("Patient.birthDate")]);

        foreach (var destinationType in new[] { DestinationType.SqlServer, DestinationType.Mongo, DestinationType.Csv })
        {
            var rules = await resolver.ResolveAsync(
                "Patient", "Patient.birthDate", destinationType, null, null, CancellationToken.None);

            rules.Should().ContainSingle($"a node's own rules apply whatever the destination is ({destinationType})");
        }
    }

    [Fact]
    public async Task Rules_are_scoped_to_their_resource_type()
    {
        var resolver = new InlineFhirResourceRuleResolver(
            [Rule("Patient.birthDate", resourceType: "Patient"),
             Rule("Observation.valueQuantity.value", resourceType: "Observation")]);

        var patientFields = await resolver.ResolveSourceFieldsAsync(
            "Patient", DestinationType.SqlServer, null, null, CancellationToken.None);

        patientFields.Should().ContainSingle().Which.Should().Be("Patient.birthDate");
    }

    /// <summary>Null ResourceType is the "any resource" wildcard, matching the convention the repository-backed
    /// tiers already use.</summary>
    [Fact]
    public async Task A_null_resource_type_applies_to_every_resource()
    {
        var resolver = new InlineFhirResourceRuleResolver([Rule("id", resourceType: null)]);

        var fields = await resolver.ResolveSourceFieldsAsync(
            "Observation", DestinationType.SqlServer, null, null, CancellationToken.None);

        fields.Should().ContainSingle();
    }

    [Fact]
    public async Task Resource_types_match_case_insensitively()
    {
        var resolver = new InlineFhirResourceRuleResolver([Rule("Patient.birthDate", resourceType: "patient")]);

        var fields = await resolver.ResolveSourceFieldsAsync(
            "Patient", DestinationType.SqlServer, null, null, CancellationToken.None);

        fields.Should().ContainSingle();
    }

    /// <summary>Rules for one source field chain in Order — the repository resolver already guarantees this, so
    /// the inline one must too or the same rule set would behave differently depending on where it was read.</summary>
    [Fact]
    public async Task Rules_for_one_field_are_ordered()
    {
        var resolver = new InlineFhirResourceRuleResolver(
        [
            Rule("Patient.birthDate", TransformNodeType.StringNormalization, order: 2),
            Rule("Patient.birthDate", TransformNodeType.DateTimeFormat, order: 1),
        ]);

        var rules = await resolver.ResolveAsync(
            "Patient", "Patient.birthDate", DestinationType.SqlServer, null, null, CancellationToken.None);

        rules.Select(r => r.Order).Should().ContainInOrder(1, 2);
    }

    [Fact]
    public async Task Disabled_rules_are_excluded()
    {
        var disabled = Rule("Patient.birthDate");
        disabled.SetEnabled(false);

        var resolver = new InlineFhirResourceRuleResolver([disabled]);

        var fields = await resolver.ResolveSourceFieldsAsync(
            "Patient", DestinationType.SqlServer, null, null, CancellationToken.None);

        fields.Should().BeEmpty();
        resolver.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task An_unmatched_source_field_returns_nothing()
    {
        var resolver = new InlineFhirResourceRuleResolver([Rule("Patient.birthDate")]);

        var rules = await resolver.ResolveAsync(
            "Patient", "Patient.gender", DestinationType.SqlServer, null, null, CancellationToken.None);

        rules.Should().BeEmpty();
    }
}
