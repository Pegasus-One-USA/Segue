namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// How many records share one blob. Independent of <see cref="BlobRecordMode"/> — that axis only means
/// anything once <see cref="Individual"/> puts one record in its own blob to begin with.
/// </summary>
public enum BlobDeliveryGranularity
{
    /// <summary>The whole batch becomes one new timestamped NDJSON blob per pipeline run — object storage's
    /// natural "write-once" shape, suited to a data-lake/audit-trail consumer. No per-record identity.</summary>
    Bulk = 0,

    /// <summary>Each record gets its own blob. Combine with <see cref="BlobRecordMode"/> to decide whether a
    /// record's blob is always freshly added, created-or-overwritten, or only ever overwritten.</summary>
    Individual = 1,
}
