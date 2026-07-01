namespace FHIRBridge.Application.Abstractions.Normalization;

/// <summary>
/// Resolves a patient identity against a Master Patient Index using the FHIR <c>Patient/$match</c> operation. Returns
/// the matched canonical patient id (or null when no confident match / not configured) so the normalization step can
/// link records to a single master identity. When unavailable, callers fall back to deterministic matching.
/// </summary>
public interface IPatientMatchService
{
    /// <summary>True when a $match endpoint is configured and should be consulted.</summary>
    bool IsEnabled { get; }

    /// <summary>Returns the best-match canonical Patient id, or null if there is no confident match.</summary>
    Task<string?> MatchAsync(string patientResourceJson, CancellationToken cancellationToken);
}
