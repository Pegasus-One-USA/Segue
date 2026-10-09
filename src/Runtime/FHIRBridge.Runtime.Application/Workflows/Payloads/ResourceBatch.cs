using System.Text;
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
    /// Held in memory for the run only: never serialized (prior-node snapshots, checkpoints), never logged — and
    /// left out of the record's generated ToString and Equals below, which [JsonIgnore] does not reach.
    /// </summary>
    [JsonIgnore]
    public string? PreDeIdentificationPayload { get; init; }

    // A record's generated ToString prints every public property, so without this any log line, exception message
    // or telemetry dump of an envelope would carry the full unredacted resource — as one flat string the PHI-masking
    // log enricher cannot pick apart.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ResourceType = ").Append(ResourceType)
            .Append(", ResourceId = ").Append(ResourceId)
            .Append(", Payload = ").Append(Payload);
        return true;
    }

    // Two envelopes for the same resource are equal whether or not one still carries its unredacted copy: the copy
    // is a run-local side channel, not part of the resource's identity.
    public bool Equals(ResourceEnvelope? other) =>
        other is not null
        && ResourceType == other.ResourceType
        && ResourceId == other.ResourceId
        && Equals(Payload, other.Payload);

    public override int GetHashCode() => HashCode.Combine(ResourceType, ResourceId, Payload);
}
