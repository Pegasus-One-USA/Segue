using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
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
}
