using System.Runtime.CompilerServices;

namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

/// <summary>
/// The in-memory side channel that lets a Mapping node run a column's Transformations on its real values and apply
/// the column's De-identification rule to their result (given ["DB","Tester"] → "DB Tester" → "*****ster"), while
/// everything else in the run only ever sees the redacted resource.
///
/// Why a side table rather than a property on <see cref="ResourceEnvelope"/>:
/// <list type="bullet">
/// <item>Nothing can serialize, print or compare it. A property is reachable by every serializer (an attribute such
/// as [JsonIgnore] guards one), by a record's generated ToString/Equals, and by any log line or exception message
/// that formats the envelope. This table is reachable only through the two methods below.</item>
/// <item>It cannot go stale. It is keyed on the exact envelope INSTANCE the De-identification node emitted: any node
/// that replaces the payload makes a new envelope (`with` or `new`), which has no entry. And it is honoured only
/// while the payload is still the exact redacted JSON string it was taken alongside — a string cannot be mutated in
/// place, and any other (mutable) payload type is refused outright.</item>
/// <item>It holds the minimum. The stored JSON is the REDACTED resource with only the fields a deferrable rule
/// (Mask/Redact/Remove) covered restored — see DeIdentificationResult.DeferrableOriginalJson — not the full original.</item>
/// <item>Its lifetime is the envelope's. Entries are weak-keyed, so they are released with the run's node outputs;
/// they are not dropped on first read because one De-identification output can feed several Mapping nodes (one per
/// destination).</item>
/// </list>
/// </summary>
public static class DeIdentificationSnapshots
{
    private static readonly ConditionalWeakTable<ResourceEnvelope, Snapshot> Table = new();

    /// <summary>Records, for this exact envelope, the JSON a deferred column may be read from.</summary>
    public static void Attach(ResourceEnvelope envelope, string deferrableOriginalJson, string redactedJson) =>
        Table.AddOrUpdate(envelope, new Snapshot(deferrableOriginalJson, redactedJson));

    /// <summary>
    /// The JSON a deferred column may be read from — or null when this envelope has none, or its payload is no
    /// longer the exact redacted JSON string the snapshot was taken alongside.
    /// </summary>
    public static string? DeferrableOriginalIfCurrent(ResourceEnvelope envelope) =>
        Table.TryGetValue(envelope, out var snapshot)
        && envelope.Payload is string payload
        && string.Equals(payload, snapshot.RedactedJson, StringComparison.Ordinal)
            ? snapshot.DeferrableOriginalJson
            : null;

    /// <summary>A private type, so nothing outside this class can reach or format what it holds.</summary>
    private sealed class Snapshot
    {
        public Snapshot(string deferrableOriginalJson, string redactedJson)
        {
            DeferrableOriginalJson = deferrableOriginalJson;
            RedactedJson = redactedJson;
        }

        public string DeferrableOriginalJson { get; }

        public string RedactedJson { get; }

        public override string ToString() => nameof(Snapshot);
    }
}
