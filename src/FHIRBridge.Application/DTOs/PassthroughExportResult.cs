namespace FHIRBridge.Application.DTOs;

/// <summary>
/// The outcome of formatting a pass-through export into the tenant's admin-configured destination format.
/// <para>
/// <b>Inline</b> formats (CSV, NDJSON) carry the serialized payload in <see cref="Content"/> for the caller to return
/// in the HTTP body. <b>Sink</b> formats (blob, SQL, S3, …) are written to the configured target by the destination
/// writer; <see cref="Content"/> is null and <see cref="WrittenCount"/> reports how many records were written.
/// </para>
/// </summary>
public sealed record PassthroughExportResult(
    bool Inline,
    string? Content,
    string ContentType,
    string? FileName,
    int RecordCount,
    int WrittenCount,
    string Format);
