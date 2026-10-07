using System.Text.Json.Serialization;

namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record ResourceBatch(IReadOnlyCollection<ResourceEnvelope> Resources);

public sealed record ResourceEnvelope(string ResourceType, string ResourceId, object Payload)
{
    /// <summary>
    /// The resource exactly as it was BEFORE a De-identification node redacted it, set only by that node and only
    /// when it actually redacted something. The Mapping node reads a column from it when that column has its own
    /// Transformations and its source is covered by a De-identification rule, so the Transformations run on the
    /// real values and the redaction is applied to their result (e.g. given ["DB","Tester"] → "DB Tester" →
    /// "*****ster", not "DB **ster"). <see cref="Payload"/> stays the redacted resource for everything else.
    /// Held in memory for the run only: never serialized (prior-node snapshots, checkpoints), never logged.
    /// </summary>
    [JsonIgnore]
    public string? PreDeIdentificationPayload { get; init; }
}
