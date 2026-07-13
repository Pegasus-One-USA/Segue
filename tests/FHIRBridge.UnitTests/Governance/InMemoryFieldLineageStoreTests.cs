using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class InMemoryFieldLineageStoreTests
{
    [Fact]
    public async Task GetFieldsAsync_returns_only_entries_for_the_requested_resource()
    {
        var store = new InMemoryFieldLineageStore();
        var now = DateTime.UtcNow;

        await store.AppendAsync(
            new FieldLineageRecord(Guid.NewGuid(), null, "Observation", "obs1", "Observation.code.coding[0].display",
                "TerminologyTranslation", "dbo.Observation", "ObservationName", now),
            CancellationToken.None);
        await store.AppendAsync(
            new FieldLineageRecord(Guid.NewGuid(), null, "Observation", "obs2", "Observation.code.coding[0].display",
                "TerminologyTranslation", "dbo.Observation", "ObservationName", now),
            CancellationToken.None);

        var fields = await store.GetFieldsAsync(new FieldLineageQuery("Observation", "obs1"), CancellationToken.None);

        fields.Should().ContainSingle(f => f.SourceResourceId == "obs1");
    }

    [Fact]
    public async Task PurgeOlderThanAsync_removes_only_entries_older_than_the_cutoff()
    {
        var store = new InMemoryFieldLineageStore();
        var cutoff = DateTime.UtcNow;

        await store.AppendAsync(
            new FieldLineageRecord(Guid.NewGuid(), null, "Observation", "old", "path", "DirectCopy", null, "col",
                cutoff.AddDays(-2)),
            CancellationToken.None);
        await store.AppendAsync(
            new FieldLineageRecord(Guid.NewGuid(), null, "Observation", "new", "path", "DirectCopy", null, "col",
                cutoff.AddDays(2)),
            CancellationToken.None);

        var purged = await store.PurgeOlderThanAsync(cutoff, CancellationToken.None);

        purged.Should().Be(1);
        var remaining = await store.GetFieldsAsync(new FieldLineageQuery("Observation", "new"), CancellationToken.None);
        remaining.Should().ContainSingle();
    }
}
