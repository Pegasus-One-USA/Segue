using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Outcome of a single destination write. <see cref="InlineDownload"/> and <see cref="DownloadUrl"/> are populated
/// only by the (currently CSV-only) <see cref="ArtifactDeliveryMode.Download"/>/<see cref="ArtifactDeliveryMode.DownloadUrl"/>
/// delivery modes; every other writer/mode leaves them null and this is just a count, exactly as before.
/// </summary>
/// <param name="Count">Records actually written. A writer that isolates per-record failures (see
/// <see cref="RecordErrors"/>) reports only the successful count here — not the batch size — so pipeline-run
/// stats reflect what really landed in the destination.</param>
/// <param name="RecordErrors">One message per record that failed to write (e.g. a constraint violation on that
/// row's data) while the rest of the batch still succeeded. Null/empty when every record either succeeded or the
/// writer doesn't support per-record isolation, in which case a write failure still throws and fails the whole
/// route as before.</param>
/// <param name="WrittenResourceIds">The <c>SourceResourceId</c> of each record that was actually written, for a
/// writer that isolates per-record failures (see <see cref="RecordErrors"/>) — so execution-history "stored"
/// bookkeeping reflects only what really landed, not the whole attempted batch. Null for a writer that doesn't
/// isolate per-record failures (the caller falls back to treating <see cref="Count"/> as "the whole batch", as
/// before).</param>
public sealed record DestinationWriteResult(
    int Count,
    GeneratedFile? InlineDownload = null,
    string? DownloadUrl = null,
    IReadOnlyList<string>? RecordErrors = null,
    IReadOnlyList<string?>? WrittenResourceIds = null);
