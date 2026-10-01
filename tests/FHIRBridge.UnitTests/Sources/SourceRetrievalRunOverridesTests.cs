using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SourceRetrievalRunOverridesTests
{
    private static SourceRetrievalConfiguration Saved() =>
        new("search-rest", ["Patient"], "identifier=1,2", true, groupId: "saved-group");

    [Fact]
    public void WithRunOverrides_ReplacesProvidedValues_AndLeavesTheSavedConfigurationAlone()
    {
        var saved = Saved();

        var run = saved.WithRunOverrides("run-group", "identifier=9");

        run.GroupId.Should().Be("run-group");
        run.SearchCriteria.Should().Be("identifier=9");
        saved.GroupId.Should().Be("saved-group");
        saved.SearchCriteria.Should().Be("identifier=1,2");
    }

    [Fact]
    public void WithRunOverrides_NullKeepsSaved_AndEmptyClears()
    {
        var kept = Saved().WithRunOverrides(null, null);
        kept.GroupId.Should().Be("saved-group");
        kept.SearchCriteria.Should().Be("identifier=1,2");

        var cleared = Saved().WithRunOverrides("", "  ");
        cleared.GroupId.Should().BeNull();
        cleared.SearchCriteria.Should().BeNull();
    }

    [Fact]
    public void WithRunOverrides_KeepsEverythingElse()
    {
        var run = Saved().WithRunOverrides("g", "c");

        run.RetrievalMethod.Should().Be("search-rest");
        run.ResourceTypes.Should().Equal("Patient");
        run.IncrementalSyncEnabled.Should().BeTrue();
    }
}
