namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

/// <param name="DownloadUrl">Populated only for a CSV destination using Download-URL delivery — persisted here (not
/// just in the transient node-output metadata) so the Execution History screen can still show a working download
/// link for this node after the run completes.</param>
/// <param name="EmailDelivery">Populated only for a CSV destination using Email delivery — the full send detail
/// (recipients, subject, body, attachment names, and outcome) so Execution History can show exactly what was
/// emailed, instead of just a record count.</param>
public sealed record DestinationWriteResult(
    string DestinationId,
    int RecordsWritten,
    DateTimeOffset WrittenAt,
    string? DownloadUrl = null,
    EmailDeliveryDetail? EmailDelivery = null);

/// <summary>Runtime-plane mirror of <c>FHIRBridge.Application.Abstractions.Destinations.EmailDeliveryDetail</c> —
/// duplicated rather than referenced because Runtime.Application deliberately never depends on the Configured
/// Pipeline's Application layer (see the two-stack layering in CLAUDE.md).</summary>
public sealed record EmailDeliveryDetail(
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string Body,
    IReadOnlyList<string> AttachmentNames,
    string Status,
    string? Error = null);
