using System.Text.Json;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Dual-read is what makes the migration safe: readers must handle the legacy flat shape and the enveloped
/// shape without being told which one they were given, so old and new node config can coexist while the
/// migration is verified. See docs/backend/18-workflow-self-contained-config-plan.md §3.
/// </summary>
public sealed class WorkflowNodeConfigurationEnvelopeTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_legacy_flat_node_is_read_at_the_root()
    {
        var settings = WorkflowNodeConfigurationEnvelope.ResolveSettings(
            Root("""{ "sourceConnectionId": "abc", "resourceTypes": ["Patient"] }"""));

        settings.TryGetProperty("sourceConnectionId", out var id).Should().BeTrue();
        id.GetString().Should().Be("abc");
    }

    [Fact]
    public void An_enveloped_node_is_read_from_its_config_object()
    {
        var settings = WorkflowNodeConfigurationEnvelope.ResolveSettings(
            Root("""{ "ref": { "masterId": "m1" }, "config": { "sourceConnectionId": "abc" } }"""));

        settings.TryGetProperty("sourceConnectionId", out var id).Should().BeTrue();
        id.GetString().Should().Be("abc");
    }

    [Fact]
    public void Provenance_is_not_visible_to_readers_of_the_settings()
    {
        var settings = WorkflowNodeConfigurationEnvelope.ResolveSettings(
            Root("""{ "ref": { "masterId": "m1" }, "config": { "baseUrl": "https://x" } }"""));

        // "ref" is portal-only metadata; an executor reaching for it would be reading the envelope, not config.
        settings.TryGetProperty("ref", out _).Should().BeFalse();
    }

    /// <summary>The trap this shape check exists for: a wizard field bag is Record&lt;string,string&gt;, so a
    /// legacy node can carry a STRING called "config". Treating that as an envelope would hide every real
    /// setting behind an empty object.</summary>
    [Fact]
    public void A_legacy_node_with_a_string_named_config_is_still_read_flat()
    {
        var root = Root("""{ "config": "some-profile-name", "destinationId": "d1" }""");

        WorkflowNodeConfigurationEnvelope.IsEnveloped(root).Should().BeFalse();

        var settings = WorkflowNodeConfigurationEnvelope.ResolveSettings(root);
        settings.TryGetProperty("destinationId", out var destination).Should().BeTrue();
        destination.GetString().Should().Be("d1");
    }

    [Fact]
    public void An_empty_config_object_still_counts_as_enveloped()
    {
        var root = Root("""{ "ref": { "masterId": "m1" }, "config": {} }""");

        WorkflowNodeConfigurationEnvelope.IsEnveloped(root).Should().BeTrue();
        WorkflowNodeConfigurationEnvelope.ResolveSettings(root)
            .EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void A_non_object_root_is_returned_unchanged_for_the_callers_own_shape_checks()
    {
        var root = Root("""[1, 2, 3]""");

        WorkflowNodeConfigurationEnvelope.IsEnveloped(root).Should().BeFalse();
        WorkflowNodeConfigurationEnvelope.ResolveSettings(root).ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Theory]
    [InlineData("""{ "destinationId": "d1" }""")]
    [InlineData("""{ "config": { "destinationId": "d1" } }""")]
    public void TryGetSettings_accepts_both_shapes(string json)
    {
        using var document = JsonDocument.Parse(json);

        WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out var settings).Should().BeTrue();
        settings.TryGetProperty("destinationId", out var id).Should().BeTrue();
        id.GetString().Should().Be("d1");
    }

    [Fact]
    public void TryGetSettings_rejects_a_non_object_document_so_callers_fall_through()
    {
        using var document = JsonDocument.Parse("\"just-a-string\"");

        WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetSettings_rejects_a_null_document()
    {
        WorkflowNodeConfigurationEnvelope.TryGetSettings(null, out _).Should().BeFalse();
    }
}
