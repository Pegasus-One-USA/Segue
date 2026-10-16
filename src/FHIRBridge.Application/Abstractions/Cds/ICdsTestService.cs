using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// Runs a pasted CDS request against one configured instance from the admin Test screen. No JWT, works on a
/// disabled instance too, and writes nothing to the CDS call log — the pasted request may hold real data.
/// </summary>
public interface ICdsTestService
{
    /// <exception cref="FHIRBridge.SharedKernel.Exceptions.NotFoundException">No instance with that id.</exception>
    Task<CdsTestResultDto> RunAsync(Guid instanceId, CdsRequest request, CancellationToken cancellationToken);
}

/// <summary>What the Test screen shows after a run.</summary>
/// <param name="Outcome">
/// The outcome the live endpoint would have recorded: "Success", "NoCards", "Degraded", "PreconditionFailed",
/// "BadRequest" or "Error" (a <see cref="FHIRBridge.Domain.Enums.CdsCallOutcome"/> name).
/// </param>
/// <param name="HttpStatus">The status the live endpoint would have returned (200, 400, 412 or 500).</param>
/// <param name="Response">The CDS response body (cards), when <paramref name="HttpStatus"/> is 200.</param>
/// <param name="DurationMs">Handler time in milliseconds.</param>
/// <param name="Messages">Short notes for the admin, e.g. "Prefetch 'allergies' missing" or the handler's degraded reason.</param>
/// <param name="ResolvedSettings">The instance's settings with defaults filled in, as the handler saw them.</param>
public sealed record CdsTestResultDto(
    string Outcome,
    int HttpStatus,
    CdsResponse? Response,
    long DurationMs,
    IReadOnlyList<string> Messages,
    JsonObject ResolvedSettings);
