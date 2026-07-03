namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Set-level (Expert Determination) de-identification. Unlike <see cref="IDeIdentificationService"/>, which operates
/// per resource, this evaluates the whole extracted cohort to enforce a statistical disclosure-control guarantee —
/// k-anonymity: every combination of quasi-identifier values must occur at least <c>k</c> times, achieved by
/// generalizing quasi-identifiers and suppressing records that remain in classes smaller than <c>k</c>.
/// </summary>
public interface IDataSetDeIdentificationService
{
    Task<DataSetDeIdentificationResult> DeIdentifyAsync(
        DataSetDeIdentificationRequest request,
        CancellationToken cancellationToken);
}

public sealed record DataSetDeIdentificationRequest(
    string ResourceType,
    IReadOnlyList<string> ResourcesJson);

public sealed record DataSetDeIdentificationResult(
    IReadOnlyList<string> ResourcesJson,
    int InputCount,
    int SuppressedCount,
    int KAnonymity)
{
    public int OutputCount => ResourcesJson.Count;
}
