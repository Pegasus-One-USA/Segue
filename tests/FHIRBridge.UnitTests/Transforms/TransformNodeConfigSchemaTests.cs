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
    [InlineData("baseUrl", "absolute")]
    [InlineData("identifierSystem", "logical")]
    public void ReferenceConstruction_scopes_a_field_to_the_single_style_that_reads_it(string key, string style)
    {
        var field = Field(TransformNodeType.ReferenceConstruction, key);

        field.VisibleWhen.Should().NotBeNull();
        field.VisibleWhen!.Key.Should().Be("style");
        field.VisibleWhen.Values.Should().Equal([style]);

        field.IsAdvanced.Should().BeFalse(
            "the style that shows it cannot produce a valid reference without it, and a required field "
            + "behind a collapsed Advanced panel is how a blank Base URL ends up writing \"/Patient/123\"");
    }

    [Theory]
    [InlineData("resourceType")]
    [InlineData("style")]
    [InlineData("display")]
    [InlineData("allowedTargetTypes")]
    public void ReferenceConstruction_leaves_every_cross_style_field_unscoped(string key)
    {
        // The rule this whole schema rests on: a field may only be scoped when its key is read nowhere
        // outside the styles it is visible for, because the config form PRUNES hidden keys on save and the
        // node then falls back to that key's default. resourceType is the trap — it looks unused under urn
        // and logical, but allowedTargetTypes is checked against it on every style before the style switch,
        // so scoping it would silently re-default it to "Patient" (see ReferenceConstructionStyleMatrixTests'
        // Allowed_target_types_are_enforced_for_every_style). display and allowedTargetTypes are likewise
        // read by all four branches.
        Field(TransformNodeType.ReferenceConstruction, key).VisibleWhen.Should().BeNull();
    }

    [Theory]
    [InlineData("index", new[] { "nth" })]
    [InlineData("separator", new[] { "join", "filter" })]
    [InlineData("predicateField", new[] { "filter" })]
    [InlineData("predicateValue", new[] { "filter" })]
    public void ArrayListOperations_scopes_each_field_to_the_operations_that_use_it(string key, string[] operations)
    {
        // Eight operations sharing one flat form put an index box on screen for "count" and an Advanced
        // Options panel on "nth" offering a predicate it will never read.
        var visibility = Field(TransformNodeType.ArrayListOperations, key).VisibleWhen;

        visibility.Should().NotBeNull();
        visibility!.Key.Should().Be("operation");
        visibility.Values.Should().Equal(operations);
    }

    [Fact]
    public void ArrayListOperations_leaves_no_advanced_fields_visible_outside_filter()
    {
        // The portal's "Advanced Options (n)" toggle counts the fields left after visibleWhen, so scoping the
        // two predicate fields to "filter" is what makes the whole panel disappear for every other operation
        // rather than sitting there empty-but-present.
        TransformNodeConfigSchemas.Get(TransformNodeType.ArrayListOperations)!.Fields
            .Where(f => f.IsAdvanced)
            .Should().OnlyContain(f => f.VisibleWhen != null && f.VisibleWhen.Values.Contains("filter"));
    }

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

    [Theory]
    [InlineData("template", "concat")]
    [InlineData("splitTemplate", "split")]
    public void ConcatenationTemplating_gives_each_mode_its_own_template_key(string key, string mode)
    {
        // A template is meaningful in both modes — concat binds the column's source fields, split binds the
        // pieces the split produced — but it means something different in each, so each mode owns its key
        // rather than sharing one. That also makes the split template un-stale-able: every rule saved before
        // this node honoured a template carries a `template` key whatever its mode, so reading that key in
        // split mode would change what existing rules write.
        var visibility = Field(TransformNodeType.ConcatenationTemplating, key).VisibleWhen;

        visibility.Should().NotBeNull();
        visibility!.Key.Should().Be("mode");
        visibility.Values.Should().Equal([mode]);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("splitTemplate")]
    public void ConcatenationTemplating_resets_a_template_when_the_mode_changes(string key)
    {
        // "{0} {1}" written against a joined column's two source fields describes nothing in particular once
        // the same rule is splitting a string into however many pieces, so neither template is carried across
        // a mode switch to be half-applied to whatever the other mode produces.
        Field(TransformNodeType.ConcatenationTemplating, key).ResetOn.Should().Equal(["mode"]);
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
