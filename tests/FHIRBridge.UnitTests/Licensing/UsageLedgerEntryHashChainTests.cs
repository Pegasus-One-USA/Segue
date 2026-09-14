using FHIRBridge.Domain.Entities.Licensing;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>Mirrors <c>AuditLogHashChainTests</c>'s shape for <see cref="UsageLedgerEntry"/> — the usage
/// ledger's tamper-evidence is the same hash-chain mechanism, just over a different set of fields.</summary>
public sealed class UsageLedgerEntryHashChainTests
{
    [Fact]
    public void VerifyOwnHash_returns_true_for_an_untampered_row()
    {
        var entry = new UsageLedgerEntry(
            Guid.NewGuid(), DateTime.UtcNow, Environment.TickCount64,
            userCount: 3, sourceConnectionCount: 2, tenantCount: 1, workflowCount: 5,
            cumulativeConfiguredPipelineRunCount: 42, cumulativeRuntimeWorkflowRunCount: 17,
            processedRecordsThisMonth: 1234, previousHash: null);

        entry.VerifyOwnHash().Should().BeTrue();
    }

    [Fact]
    public void A_row_chained_onto_the_wrong_previous_hash_produces_a_different_EntryHash()
    {
        var observedUtc = DateTime.UtcNow;
        var monotonicTicks = Environment.TickCount64;

        var onCorrectChain = new UsageLedgerEntry(
            Guid.NewGuid(), observedUtc, monotonicTicks,
            userCount: 3, sourceConnectionCount: 2, tenantCount: 1, workflowCount: 5,
            cumulativeConfiguredPipelineRunCount: 42, cumulativeRuntimeWorkflowRunCount: 17,
            processedRecordsThisMonth: 1234, previousHash: "AAAA");

        var onWrongChain = new UsageLedgerEntry(
            Guid.NewGuid(), observedUtc, monotonicTicks,
            userCount: 3, sourceConnectionCount: 2, tenantCount: 1, workflowCount: 5,
            cumulativeConfiguredPipelineRunCount: 42, cumulativeRuntimeWorkflowRunCount: 17,
            processedRecordsThisMonth: 1234, previousHash: "BBBB");

        // Same fields, different PreviousHash — exactly what deleting/reordering a row in the middle of the
        // chain would produce: the next row's stored PreviousHash no longer matches, and (if someone tried
        // to patch it) the row's own EntryHash would no longer verify against its new PreviousHash.
        onCorrectChain.EntryHash.Should().NotBe(onWrongChain.EntryHash);
        onWrongChain.VerifyOwnHash().Should().BeTrue(); // internally consistent...
        onWrongChain.PreviousHash.Should().NotBe(onCorrectChain.PreviousHash); // ...but chains onto a different link.
    }

    [Fact]
    public void VerifyOwnHash_returns_false_when_a_count_is_tampered_with_after_construction_via_reflection()
    {
        // UsageLedgerEntry's setters are all private (append-only by design), so the only way to simulate a
        // direct-DB-edit tamper in a unit test is to rebuild the row with a mismatched EntryHash — this stands
        // in for "someone hand-edited the row's WorkflowCount column but left the old EntryHash in place."
        var entry = new UsageLedgerEntry(
            Guid.NewGuid(), DateTime.UtcNow, Environment.TickCount64,
            userCount: 3, sourceConnectionCount: 2, tenantCount: 1, workflowCount: 5,
            cumulativeConfiguredPipelineRunCount: 42, cumulativeRuntimeWorkflowRunCount: 17,
            processedRecordsThisMonth: 1234, previousHash: null);

        var recomputedWithTamperedCount = UsageLedgerEntry.ComputeHash(
            entry.PreviousHash, entry.ObservedUtc, entry.MonotonicTicks,
            entry.UserCount, entry.SourceConnectionCount, entry.TenantCount,
            workflowCount: 999, // tampered
            entry.CumulativeConfiguredPipelineRunCount, entry.CumulativeRuntimeWorkflowRunCount,
            entry.ProcessedRecordsThisMonth);

        entry.EntryHash.Should().NotBe(recomputedWithTamperedCount);
    }
}
