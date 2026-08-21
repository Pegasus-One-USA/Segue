using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class FhirComplexTypeNodesTests
{
    [Fact]
    public void ReferenceConstructionNode_composes_ResourceType_slash_id()
    {
        var config = new Dictionary<string, string> { ["resourceType"] = "Patient", ["display"] = "Jane Roe" };
        var result = new ReferenceConstructionNode().Execute("123", config, null);
        var reference = (System.Text.Json.Nodes.JsonObject)result.Value!;
        reference["reference"]!.GetValue<string>().Should().Be("Patient/123");
        reference["display"]!.GetValue<string>().Should().Be("Jane Roe");
    }

    [Fact]
    public void ReferenceConstructionNode_builds_a_logical_reference_via_identifier()
    {
        var config = new Dictionary<string, string> { ["style"] = "logical", ["identifierSystem"] = "http://hl7.org/fhir/sid/us-npi" };
        var result = new ReferenceConstructionNode().Execute("123", config, null);
        var reference = (System.Text.Json.Nodes.JsonObject)result.Value!;
        reference["identifier"]!["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/sid/us-npi");
        reference["identifier"]!["value"]!.GetValue<string>().Should().Be("123");
    }

    [Fact]
    public void ReferenceConstructionNode_rejects_a_target_type_outside_the_allowed_list()
    {
        var config = new Dictionary<string, string> { ["resourceType"] = "Group", ["allowedTargetTypes"] = "Patient,RelatedPerson" };
        var result = new ReferenceConstructionNode().Execute("123", config, null);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void IdentifierFormattingNode_validates_the_NPI_check_digit()
    {
        var config = new Dictionary<string, string> { ["system"] = "http://hl7.org/fhir/sid/us-npi", ["typeCode"] = "NPI" };
        var valid = new IdentifierFormattingNode().Execute("1234567893", config, null);
        valid.Success.Should().BeTrue();

        var invalid = new IdentifierFormattingNode().Execute("1234567890", config, null);
        invalid.Success.Should().BeFalse();
    }

    [Fact]
    public void HumanNameParsingNode_parses_Last_comma_First_pattern()
    {
        var result = new HumanNameParsingNode().Execute("Roe, Jane M", new Dictionary<string, string>(), null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("Roe");
        name["given"]!.AsArray().Select(g => g!.GetValue<string>()).Should().BeEquivalentTo(["Jane", "M"]);
    }

    [Fact]
    public void HumanNameParsingNode_extracts_a_recognized_prefix_and_suffix()
    {
        var result = new HumanNameParsingNode().Execute("Dr John Smith Jr", new Dictionary<string, string>(), null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["prefix"]!.AsArray()[0]!.GetValue<string>().Should().Be("Dr");
        name["suffix"]!.AsArray()[0]!.GetValue<string>().Should().Be("Jr");
        name["family"]!.GetValue<string>().Should().Be("Smith");
    }

    [Fact]
    public void AddressParsingNode_splits_line_city_state_zip()
    {
        var result = new AddressParsingNode().Execute("123 Main St, Detroit, MI 48201", new Dictionary<string, string>(), null);
        var address = (System.Text.Json.Nodes.JsonObject)result.Value!;
        address["city"]!.GetValue<string>().Should().Be("Detroit");
        address["state"]!.GetValue<string>().Should().Be("MI");
        address["postalCode"]!.GetValue<string>().Should().Be("48201");
        address["country"]!.GetValue<string>().Should().Be("US", "the country defaults to US when not configured");
    }

    [Fact]
    public void TelecomNormalizationNode_normalizes_a_10_digit_US_number_to_E164()
    {
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", new Dictionary<string, string>(), null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("phone");
        contact["value"]!.GetValue<string>().Should().Be("+13135550142");
    }

    [Fact]
    public void TelecomNormalizationNode_detects_email_over_phone()
    {
        var result = new TelecomNormalizationNode().Execute("jane@example.com", new Dictionary<string, string>(), null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("email");
    }

    [Fact]
    public void TelecomNormalizationNode_honors_an_explicit_fax_system_override()
    {
        var config = new Dictionary<string, string> { ["system"] = "fax", ["rank"] = "1" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("fax");
        contact["rank"]!.GetValue<int>().Should().Be(1);
    }
}
