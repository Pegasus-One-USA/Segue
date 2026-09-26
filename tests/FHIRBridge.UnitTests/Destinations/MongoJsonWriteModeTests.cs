using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using MongoDB.Bson;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The MongoDB-only "store this JSON as a JSON string, or as a real (BSON) document" choice on a mapping's
/// Json-valued column. The choice rides <see cref="MappingField.Format"/> as a marker — see
/// <see cref="MappingFieldFormat"/> for why — and <see cref="MongoJsonValueConverter"/> is what turns the
/// mapping engine's raw JSON text into the nested BSON value when "document" is the one selected.
/// </summary>
public sealed class MongoJsonWriteModeTests
{
    [Theory]
    // Every mapping authored before the option existed carries one of these, and must keep the single
    // escaped-string behaviour it has always had.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wholeNodeAsJson")]
    [InlineData("directField;aggregate=csv")]
    [InlineData("wholeNodeAsJson;json=string")]
    public void ReadJsonWriteMode_defaults_to_JsonString(string? format)
        => MappingFieldFormat.ReadJsonWriteMode(format).Should().Be(JsonColumnWriteMode.JsonString);

    [Theory]
    [InlineData("json=document")]
    [InlineData("wholeNodeAsJson;json=document")]
    [InlineData("WholeNodeAsJson;JSON=DOCUMENT")]
    [InlineData("directField; json=document ")]
    public void ReadJsonWriteMode_reads_the_document_marker(string format)
        => MappingFieldFormat.ReadJsonWriteMode(format).Should().Be(JsonColumnWriteMode.Document);

    /// <summary>
    /// Format is a ';'-separated marker BAG whose segments carry arbitrary text — "joinedFields;delimiter=X"
    /// takes everything after the first '=' as the delimiter (JsonMappingEngine.ParseDelimiter) — so the
    /// marker must be matched as a whole segment. A raw substring search over the joined value would turn an
    /// unrelated joined-string column into document storage.
    /// </summary>
    [Theory]
    [InlineData("joinedFields;delimiter=json=document")]
    [InlineData("joinedFields;delimiter=|json=document")]
    [InlineData("notjson=document")]
    [InlineData("json=documentary")]
    public void ReadJsonWriteMode_does_not_match_the_marker_inside_another_segment(string format)
        => MappingFieldFormat.ReadJsonWriteMode(format).Should().Be(JsonColumnWriteMode.JsonString);

    [Fact]
    public void TryParse_turns_a_resource_into_a_nested_document()
    {
        const string json = """
            {"resourceType":"Patient","id":"p1","active":true,"name":[{"family":"Chalmers","given":["Peter","James"]}]}
            """;

        MongoJsonValueConverter.TryParse(json, out var value).Should().BeTrue();

        var document = value.AsBsonDocument;
        document["resourceType"].AsString.Should().Be("Patient");
        document["active"].AsBoolean.Should().BeTrue();
        // The point of the whole option: "name" is a real array of real sub-documents Mongo can index into
        // with a dotted path, not one opaque string.
        document["name"].AsBsonArray.Should().HaveCount(1);
        document["name"][0]["family"].AsString.Should().Be("Chalmers");
        document["name"][0]["given"].AsBsonArray.Select(v => v.AsString).Should().Equal("Peter", "James");
    }

    [Fact]
    public void TryParse_accepts_an_array_at_the_root()
    {
        MongoJsonValueConverter.TryParse("""[{"code":"a"},{"code":"b"}]""", out var value).Should().BeTrue();
        value.AsBsonArray.Select(v => v["code"].AsString).Should().Equal("a", "b");
    }

    [Fact]
    public void TryParse_keeps_decimal_precision_rather_than_widening_to_double()
    {
        MongoJsonValueConverter.TryParse("""{"value":0.1,"count":7}""", out var value).Should().BeTrue();

        var document = value.AsBsonDocument;
        document["count"].BsonType.Should().Be(BsonType.Int32);
        document["value"].BsonType.Should().Be(BsonType.Decimal128);
        document["value"].ToString().Should().Be("0.1");
    }

    [Fact]
    public void TryParse_leaves_a_dollar_prefixed_key_as_a_plain_field()
    {
        // BsonDocument.Parse would read this as Extended JSON and silently produce a BSON date. Arbitrary
        // FHIR extension content must round-trip as the object it actually is.
        MongoJsonValueConverter.TryParse("""{"outer":{"$date":"2026-09-22"}}""", out var value).Should().BeTrue();
        value["outer"]["$date"].AsString.Should().Be("2026-09-22");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"unterminated\": ")]
    public void TryParse_reports_failure_so_the_caller_can_fall_back_to_the_raw_text(string json)
    {
        MongoJsonValueConverter.TryParse(json, out var value).Should().BeFalse();
        value.Should().Be(BsonNull.Value);
    }
}
