using FHIRBridge.Application.DTOs.Transforms;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// The rule-config form renders straight from these schemas, so which fields a node exposes — and in which
/// mode — is decided here, not in the portal.
/// </summary>
public sealed class TransformNodeConfigSchemaTests
{
    private static TransformConfigFieldSchema Field(TransformNodeType nodeType, string key) =>
        TransformNodeConfigSchemas.Get(nodeType)!.Fields.Single(f => f.Key == key);

    [Theory]
    [InlineData("separator", "concat")]
    [InlineData("splitDelimiter", "split")]
    [InlineData("splitIsRegex", "split")]
    public void ConcatenationTemplating_scopes_each_field_to_the_mode_that_uses_it(string key, string mode)
    {
        // Showing a join separator and a split delimiter side by side let a rule be configured with the field
        // its mode ignores, with nothing to say the setting did nothing.
        var visibility = Field(TransformNodeType.ConcatenationTemplating, key).VisibleWhen;

        visibility.Should().NotBeNull();
        visibility!.Key.Should().Be("mode");
        visibility.Values.Should().Equal([mode]);
    }

    [Fact]
    public void ConcatenationTemplating_keeps_the_template_available_in_both_modes()
    {
        // concat binds the column's source fields; split binds the pieces the split produced.
        Field(TransformNodeType.ConcatenationTemplating, "template").VisibleWhen.Should().BeNull();
    }

    [Fact]
    public void ConcatenationTemplating_resets_the_template_when_the_mode_changes()
    {
        // The template survives the switch (it applies in both modes) but its CONTENT does not: "{0} {1}"
        // written against a joined column's two source fields describes nothing in particular once the same
        // rule is splitting a string into however many pieces. Visibility cannot express "still applies, no
        // longer valid", so the field declares the key whose change invalidates it.
        Field(TransformNodeType.ConcatenationTemplating, "template").ResetOn.Should().Equal(["mode"]);
    }

    [Theory]
    [InlineData("separator")]
    [InlineData("splitDelimiter")]
    public void ConcatenationTemplating_mode_scoped_fields_need_no_reset_rule(string key)
    {
        // A field scoped to one mode is already dropped by VisibleWhen when the mode changes — declaring a
        // reset as well would be belt-and-braces that hides which mechanism is doing the work.
        Field(TransformNodeType.ConcatenationTemplating, key).ResetOn.Should().BeNull();
    }

    [Fact]
    public void ConcatenationTemplating_labels_drop_the_mode_qualifier_now_that_the_field_is_mode_scoped()
    {
        Field(TransformNodeType.ConcatenationTemplating, "splitDelimiter").Label
            .Should().NotContain("split mode");
        Field(TransformNodeType.ConcatenationTemplating, "separator").Label
            .Should().NotContain("concat mode");
    }

    [Fact]
    public void Every_visibleWhen_points_at_a_field_that_exists_on_the_same_node()
    {
        // A visibleWhen naming a key the node does not have would hide its field forever, with no error.
        foreach (var schema in TransformNodeConfigSchemas.All)
        {
            var keys = schema.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var field in schema.Fields.Where(f => f.VisibleWhen is not null))
            {
                keys.Should().Contain(
                    field.VisibleWhen!.Key,
                    $"{schema.NodeType}.{field.Key} is gated on a sibling field that must exist");
            }
        }
    }

    [Fact]
    public void Every_visibleWhen_names_values_its_target_field_can_actually_hold()
    {
        // Gating on a value absent from the target select's options is the same silent "never shown" bug.
        foreach (var schema in TransformNodeConfigSchemas.All)
        {
            foreach (var field in schema.Fields.Where(f => f.VisibleWhen is not null))
            {
                var target = schema.Fields.Single(f => f.Key == field.VisibleWhen!.Key);
                if (target.Options is null)
                {
                    continue;
                }

                target.Options.Should().Contain(
                    field.VisibleWhen!.Values,
                    $"{schema.NodeType}.{field.Key} is gated on values of {target.Key}");
            }
        }
    }
}
