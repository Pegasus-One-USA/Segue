using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class SearchCriteriaPostFilterTests
{
    private static readonly IReadOnlyList<ResourceEnvelope> Observations =
    [
        Observation("o1", "final"),
        Observation("o2", "unknown"),
        Observation("o3", "entered-in-error"),
        Observation("o4", "final"),
        Observation("o5", "corrected"),
    ];

    [Fact]
    public void Drops_records_the_source_returned_despite_failing_a_code_filter()
    {
        var (kept, enforced) = SearchCriteriaPostFilter.Apply(Observations, "status=final");

        kept.Select(r => r.ResourceId).Should().Equal("o1", "o4");
        enforced.Should().Equal("status");
    }

    [Fact]
    public void Comma_separated_codes_are_ored_and_a_system_prefix_is_ignored()
    {
        var (kept, _) = SearchCriteriaPostFilter.Apply(
            Observations, "status=http://hl7.org/fhir/observation-status|final,corrected");

        kept.Select(r => r.ResourceId).Should().Equal("o1", "o4", "o5");
    }

    [Fact]
    public void Not_modifier_excludes_the_listed_codes()
    {
        var (kept, enforced) = SearchCriteriaPostFilter.Apply(Observations, "status:not=entered-in-error");

        kept.Select(r => r.ResourceId).Should().Equal("o1", "o2", "o4", "o5");
        enforced.Should().Equal("status:not");
    }

    [Fact]
    public void Removes_nothing_when_the_source_already_applied_the_filter()
    {
        IReadOnlyList<ResourceEnvelope> alreadyFiltered = [Observation("o1", "final"), Observation("o4", "final")];

        var (kept, _) = SearchCriteriaPostFilter.Apply(alreadyFiltered, "status=final");

        kept.Select(r => r.ResourceId).Should().Equal("o1", "o4");
    }

    [Fact]
    public void Leaves_non_enforceable_parameters_to_the_server()
    {
        var (kept, enforced) = SearchCriteriaPostFilter.Apply(
            Observations, "category=laboratory&date=ge2024-01-01&code=1234-5&patient=p1&_lastUpdated=gt2024-01-01");

        kept.Should().HaveCount(Observations.Count);
        enforced.Should().BeEmpty();
    }

    [Fact]
    public void Does_not_filter_on_an_element_no_record_carries()
    {
        // gender is not an Observation element — filtering on it would empty the result, so it's left alone.
        var (kept, enforced) = SearchCriteriaPostFilter.Apply(Observations, "gender=female&status=final");

        kept.Select(r => r.ResourceId).Should().Equal("o1", "o4");
        enforced.Should().Equal("status");
    }

    [Fact]
    public void Ignores_an_unsupported_modifier()
    {
        var (kept, enforced) = SearchCriteriaPostFilter.Apply(Observations, "status:missing=true");

        kept.Should().HaveCount(Observations.Count);
        enforced.Should().BeEmpty();
    }

    private static ResourceEnvelope Observation(string id, string status) =>
        new("Observation", id, $$"""{"resourceType":"Observation","id":"{{id}}","status":"{{status}}"}""", null, null);
}
