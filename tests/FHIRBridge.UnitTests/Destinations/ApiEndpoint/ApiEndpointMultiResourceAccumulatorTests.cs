using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.ApiEndpoint;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FHIRBridge.UnitTests.Destinations.ApiEndpoint;

/// <summary>
/// Covers the PR #210 review finding: with no eviction at all, an incomplete (never-completing) multi-resource
/// write held its mapped PHI in process memory for the rest of the process's life, unbounded. The age-based
/// sweep isn't exercised here directly — it turns on wall-clock time (DateTimeOffset.UtcNow), and this codebase
/// has no injectable clock abstraction anywhere to fake that without inventing one just for this test — but the
/// size-cap backstop is deterministic and is what's covered below.
/// </summary>
public sealed class ApiEndpointMultiResourceAccumulatorTests
{
    private static MappingProfile Mapping(string resourceType) =>
        new("Api Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), resourceType, []);

    private static MappedDestinationRecord Record(string resourceType, string id) =>
        new(Guid.NewGuid(), resourceType, resourceType, id, new Dictionary<string, object?> { ["Id"] = id }, null);

    [Fact]
    public void A_completed_entry_is_returned_once_and_then_gone()
    {
        var accumulator = new ApiEndpointMultiResourceAccumulator(NullLogger<ApiEndpointMultiResourceAccumulator>.Instance);
        var destinationId = Guid.NewGuid();
        var pipelineRunId = Guid.NewGuid();

        accumulator.Add(destinationId, pipelineRunId, Mapping("Patient"), [Record("Patient", "p1")]);
        accumulator.TryTakeComplete(destinationId, pipelineRunId, ["Patient", "Encounter"], out _).Should().BeFalse(
            "Encounter hasn't landed yet");

        accumulator.Add(destinationId, pipelineRunId, Mapping("Encounter"), [Record("Encounter", "e1")]);
        var completed = accumulator.TryTakeComplete(destinationId, pipelineRunId, ["Patient", "Encounter"], out var batches);

        completed.Should().BeTrue();
        batches.Should().HaveCount(2);

        // A second call for the same (destination, run) after the first take must find nothing left — the
        // interface's own contract ("the accumulator's own copy ... is cleared") is what stops a stale
        // combined write being re-delivered on any later, coincidental re-check.
        accumulator.TryTakeComplete(destinationId, pipelineRunId, ["Patient", "Encounter"], out var secondBatches)
            .Should().BeFalse();
        secondBatches.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_contribution_still_counts_toward_completing_the_set()
    {
        // The other half of PR #210's fix: MappedApiEndpointDestinationWriter now calls Add() even when a
        // resource type produced zero records this run — the accumulator itself must accept that and let it
        // complete the set, not just tolerate being called with an empty collection.
        var accumulator = new ApiEndpointMultiResourceAccumulator(NullLogger<ApiEndpointMultiResourceAccumulator>.Instance);
        var destinationId = Guid.NewGuid();
        var pipelineRunId = Guid.NewGuid();

        accumulator.Add(destinationId, pipelineRunId, Mapping("Patient"), [Record("Patient", "p1")]);
        accumulator.Add(destinationId, pipelineRunId, Mapping("Encounter"), []);

        var completed = accumulator.TryTakeComplete(destinationId, pipelineRunId, ["Patient", "Encounter"], out var batches);

        completed.Should().BeTrue();
        batches.Should().HaveCount(2);
        batches.Single(b => b.MappingProfile.ResourceType == "Encounter").Records.Should().BeEmpty();
    }

    [Fact]
    public void An_incomplete_set_that_never_arrives_does_not_grow_the_accumulator_without_bound()
    {
        // Simulates the exact scenario the review flagged: a resource type that never arrives leaves its
        // (destination, run) entry pending forever with nothing ever completing it. Without the size-cap
        // backstop, adding more distinct incomplete runs than the cap would let the accumulator's own pending
        // set — each holding real mapped PHI — grow indefinitely. Pushing well past the cap and asserting
        // PendingEntryCount never exceeds it is the deterministic half of that fix (the age-based sweep is the
        // other half, not exercised here — see the class doc).
        var accumulator = new ApiEndpointMultiResourceAccumulator(NullLogger<ApiEndpointMultiResourceAccumulator>.Instance);
        const int cap = 1_000; // must match ApiEndpointMultiResourceAccumulator.MaxPendingEntries
        const int pendingEntriesWellPastTheCap = cap + 100;

        for (var i = 0; i < pendingEntriesWellPastTheCap; i++)
        {
            // Only Patient ever lands — Encounter (the run's other expected resource type) never does, so
            // every one of these entries stays incomplete and pending, exactly like the review's scenario.
            accumulator.Add(Guid.NewGuid(), Guid.NewGuid(), Mapping("Patient"), [Record("Patient", $"p{i}")]);
        }

        accumulator.PendingEntryCount.Should().BeLessThanOrEqualTo(
            cap, "the cap must actually evict old entries, not merely let new adds keep succeeding past it");

        // The most recently added entry must still be present (eviction always takes the oldest entry by
        // timestamp, never a brand-new one).
        var lastDestinationId = Guid.NewGuid();
        var lastPipelineRunId = Guid.NewGuid();
        accumulator.Add(lastDestinationId, lastPipelineRunId, Mapping("Patient"), [Record("Patient", "last")]);
        accumulator.Add(lastDestinationId, lastPipelineRunId, Mapping("Encounter"), [Record("Encounter", "last")]);

        accumulator.TryTakeComplete(lastDestinationId, lastPipelineRunId, ["Patient", "Encounter"], out var batches)
            .Should().BeTrue("the cap must evict old abandoned entries, never block a new, genuinely completable one");
        batches.Should().HaveCount(2);
    }
}
