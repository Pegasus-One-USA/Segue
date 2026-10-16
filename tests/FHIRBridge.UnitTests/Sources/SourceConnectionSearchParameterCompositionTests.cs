using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SourceConnectionSearchParameterCompositionTests
{
    [Fact]
    public void Saved_connection_search_criteria_is_not_applied_but_advanced_search_options_are()
    {
        // The source form no longer shows Search Criteria; a value saved before it was removed must not filter runs.
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest",
            ["Patient"],
            searchCriteria: "identifier=a-195900.E-60176",
            incrementalSyncEnabled: false,
            sortOrder: "-_lastUpdated",
            includeParameters: ["Patient:general-practitioner"],
            revIncludeParameters: ["Provenance:target"]);

        SourceConnectionRuntimeResolver.ComposeSearchParameters(null, retrieval).Should()
            .Be("_sort=-_lastUpdated&_include=Patient:general-practitioner&_revinclude=Provenance:target");
    }

    [Fact]
    public void Node_search_parameters_are_kept_ahead_of_advanced_search_options()
    {
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest", ["Patient"], searchCriteria: "identifier=OLD", incrementalSyncEnabled: false, sortOrder: "name");

        SourceConnectionRuntimeResolver.ComposeSearchParameters("active=true", retrieval).Should()
            .Be("active=true&_sort=name");
    }

    [Fact]
    public void Run_search_criteria_apply_for_a_connection_with_a_saved_retrieval_config()
    {
        // Regression: the run's criteria used to be routed into the retrieval config's SearchCriteria, which
        // ComposeSearchParameters no longer reads, so they were silently dropped for every such connection.
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest", ["Patient"], searchCriteria: "identifier=SAVED", incrementalSyncEnabled: false, sortOrder: "name")
            .WithRunOverrides(groupId: null, searchCriteria: "identifier=RUN-1");
        var searchParameters = SourceConnectionRuntimeResolver.RunSearchParameters(
            "identifier=NODE-SNAPSHOT", new RunSourceOverrides(null, "identifier=RUN-1"));

        SourceConnectionRuntimeResolver.ComposeSearchParameters(searchParameters, retrieval).Should()
            .Be("identifier=RUN-1&_sort=name");
    }

    [Theory]
    [InlineData(null, "active=true")]
    [InlineData("", null)]
    [InlineData("  gender=male ", "gender=male")]
    public void Run_search_criteria_replace_the_node_string_and_blank_clears_it(string? runCriteria, string? expected)
    {
        SourceConnectionRuntimeResolver.RunSearchParameters("active=true", new RunSourceOverrides(null, runCriteria))
            .Should().Be(expected);
    }
}
