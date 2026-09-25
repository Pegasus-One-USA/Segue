using FHIRBridge.Domain.Entities;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// Pins the append semantics of per-resource-type search criteria: the "Criteria" button on each row of the
/// destination wizard's Map-fields step ADDS to whatever that resource type already has, rather than replacing it.
///
/// The de-duplication is the load-bearing part, not a tidiness detail — re-sending a key that is already present
/// is not additive filtering. Epic rejects a repeated identifier outright ("Don't support searching by IDENTIFIER
/// AND IDENTIFIER"), so a naive concatenation would turn a second save into a failed extraction. The same rule is
/// implemented in three places that must agree: here, <c>SourceConnectionRuntimeResolver.ComposeSearchParameters</c>
/// (connection-level), and the portal's own <c>mergeCriteria</c> (so the dialog previews what will be stored).
/// </summary>
public sealed class ResourceTypeCriteriaTests
{
    private static ResourceTypeCriteria NewCriteria(string criteria) =>
        new(Guid.NewGuid(), "source-1", "Patient", criteria);

    [Fact]
    public void Criteria_are_stored_as_authored()
    {
        NewCriteria("birthdate=gt2000-01-01&gender=female")
            .Criteria.Should().Be("birthdate=gt2000-01-01&gender=female");
    }

    [Fact]
    public void A_leading_question_mark_and_stray_ampersands_are_normalized_away()
    {
        // The field accepts a value pasted straight out of a URL's query string.
        NewCriteria("?gender=female&").Criteria.Should().Be("gender=female");
    }

    [Fact]
    public void Appending_adds_to_the_existing_criteria()
    {
        var criteria = NewCriteria("gender=female");

        criteria.AppendCriteria("birthdate=gt2000-01-01");

        criteria.Criteria.Should().Be("gender=female&birthdate=gt2000-01-01");
    }

    [Fact]
    public void Appending_to_empty_criteria_just_becomes_the_new_value()
    {
        // The first save for a resource type: nothing stored yet, so there is nothing to merge with.
        ResourceTypeCriteria.MergeCriteria(null, "gender=female").Should().Be("gender=female");
    }

    [Fact]
    public void Appending_a_key_that_is_already_present_keeps_the_existing_value()
    {
        var criteria = NewCriteria("identifier=MRN12345");

        criteria.AppendCriteria("identifier=MRN99999");

        // Not "identifier=MRN12345&identifier=MRN99999" — see this class's remarks on Epic.
        criteria.Criteria.Should().Be("identifier=MRN12345");
    }

    [Fact]
    public void Appending_keeps_only_the_new_parameters_whose_keys_are_absent()
    {
        var criteria = NewCriteria("gender=female&identifier=MRN12345");

        criteria.AppendCriteria("identifier=MRN99999&birthdate=gt2000-01-01");

        criteria.Criteria.Should().Be("gender=female&identifier=MRN12345&birthdate=gt2000-01-01");
    }

    [Fact]
    public void Key_comparison_ignores_case()
    {
        ResourceTypeCriteria.MergeCriteria("Gender=female", "gender=male").Should().Be("Gender=female");
    }

    [Fact]
    public void Replacing_overwrites_rather_than_merging()
    {
        var criteria = NewCriteria("gender=female&identifier=MRN12345");

        criteria.ReplaceCriteria("birthdate=gt2000-01-01");

        // The only way to remove or rewrite a parameter an append can never displace.
        criteria.Criteria.Should().Be("birthdate=gt2000-01-01");
    }

    [Fact]
    public void Replacing_with_an_empty_value_clears_the_criteria()
    {
        var criteria = NewCriteria("gender=female");

        criteria.ReplaceCriteria("");

        criteria.Criteria.Should().BeEmpty();
    }

    [Fact]
    public void A_parameter_with_no_value_is_still_deduplicated_by_its_own_text()
    {
        // e.g. a bare flag-style parameter; the whole segment is the key when there is no '='.
        ResourceTypeCriteria.MergeCriteria("_summary", "_summary").Should().Be("_summary");
    }
}
