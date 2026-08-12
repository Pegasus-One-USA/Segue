namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// Only meaningful when <see cref="BlobDeliveryGranularity.Individual"/> is selected — decides what happens
/// to a single record's blob relative to whatever's already there under that same key.
/// </summary>
public enum BlobRecordMode
{
    /// <summary>Always write a brand-new blob, never overwriting one that already exists under the same key —
    /// the blob name is suffixed with a fresh timestamp+GUID every time specifically so it never collides.</summary>
    Insert = 0,

    /// <summary>Write to the key-derived blob path unconditionally — creates it if absent, overwrites it if
    /// present. The natural behavior of a plain blob upload; no existence check needed.</summary>
    Upsert = 1,

    /// <summary>Only overwrite the key-derived blob path if it already exists; if it doesn't, the record is
    /// skipped entirely (never creates a new blob). Requires one extra existence check per record.</summary>
    Update = 2,
}
