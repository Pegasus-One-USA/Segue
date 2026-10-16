using FHIRBridge.Infrastructure.Sources;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SourceEndpointProbeSearchParameterTests
{
    [Fact]
    public void Reads_each_resource_types_declared_search_parameters_with_common_ones_folded_in()
    {
        const string capabilityStatement = """
            {
              "resourceType": "CapabilityStatement",
              "rest": [{
                "searchParam": [{ "name": "_lastUpdated" }],
                "resource": [
                  { "type": "Observation", "searchParam": [{ "name": "patient" }, { "name": "category" }, { "name": "code" }] },
                  { "type": "Patient", "searchParam": [{ "name": "identifier" }, { "name": "gender" }] },
                  { "type": "Binary", "interaction": [{ "code": "read" }] }
                ]
              }]
            }
            """;

        var byResourceType = SourceEndpointProbeService.ParseSearchParameters(capabilityStatement);

        byResourceType["Observation"].Should().Equal("patient", "category", "code", "_lastUpdated");
        byResourceType["Patient"].Should().Equal("identifier", "gender", "_lastUpdated");
        // Declares no search parameters at all — unknown, so it's left out rather than reported as "supports nothing".
        byResourceType.Should().NotContainKey("Binary");
    }

    [Fact]
    public void Returns_nothing_for_a_statement_without_rest()
    {
        SourceEndpointProbeService.ParseSearchParameters("""{"resourceType":"CapabilityStatement"}""")
            .Should().BeEmpty();
    }
}
