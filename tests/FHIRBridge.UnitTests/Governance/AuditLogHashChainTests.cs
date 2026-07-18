using FHIRBridge.Domain.Entities.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class AuditLogHashChainTests
{
    [Fact]
    public void VerifyOwnHash_returns_true_for_an_untampered_row()
    {
        var entry = new AuditLog(
            Guid.NewGuid(), DateTime.UtcNow, "alice@example.com", "SourceConnection", "Created",
            "SourceConnection", "conn-1", null, null, "{\"Name\":\"Epic Sandbox\"}", "Success", null,
            "203.0.113.10", "test-agent", "CORR-1", previousHash: null);

        entry.VerifyOwnHash().Should().BeTrue();
    }

    [Fact]
    public void A_row_chained_onto_the_wrong_previous_hash_produces_a_different_EntryHash()
    {
        var onCorrectChain = new AuditLog(
            Guid.NewGuid(), DateTime.UtcNow, "bob@example.com", "DestinationConfiguration", "Updated",
            "DestinationConfiguration", "dest-1", null, "{\"Port\":22}", "{\"Port\":2222}", "Success", null,
            null, null, "CORR-2", previousHash: "AAAA");

        var onWrongChain = new AuditLog(
            Guid.NewGuid(), onCorrectChain.OccurredOnUtc, "bob@example.com", "DestinationConfiguration", "Updated",
            "DestinationConfiguration", "dest-1", null, "{\"Port\":22}", "{\"Port\":2222}", "Success", null,
            null, null, "CORR-2", previousHash: "BBBB");

        // Same fields, different PreviousHash — this is exactly what deleting/reordering a row in the middle
        // of the chain would produce: the next row's stored PreviousHash no longer matches, and (if someone
        // tried to patch it) the row's own EntryHash would no longer verify against its new PreviousHash.
        onCorrectChain.EntryHash.Should().NotBe(onWrongChain.EntryHash);
        onWrongChain.VerifyOwnHash().Should().BeTrue(); // internally consistent...
        onWrongChain.PreviousHash.Should().NotBe(onCorrectChain.PreviousHash); // ...but chains onto a different link.
    }
}
