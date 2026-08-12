namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// How a batch of mapped records becomes blob(s). <see cref="Append"/> is the long-standing default (one new
/// timestamped NDJSON blob per pipeline run — object storage's natural "write-once, never touch again" shape,
/// suited to a data-lake/audit-trail consumer). <see cref="Upsert"/> gives Blob real "update a record" semantics
/// like SQL/Mongo already have: one blob per record, named by an upsert-key field, overwritten in place on the
/// next run instead of accumulating a new file every time.
/// </summary>
public enum BlobWriteMode
{
    Append = 0,
    Upsert,
}
