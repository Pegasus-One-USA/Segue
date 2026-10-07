using FHIRBridge.Application.Abstractions.Destinations;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>No master patient index yet: every answer is "none available", so a patient an eClinicalWorks or
/// athenahealth target cannot find by identifier waits as <c>patient-awaiting-mpi</c> instead of being guessed. The MPI
/// replaces this registration.</summary>
public sealed class UnavailableEhrTargetPatientMatcher : IEhrTargetPatientMatcher
{
    public Task<EhrPatientMatchOutcome?> MatchAsync(EhrTargetPatientMatchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<EhrPatientMatchOutcome?>(null);
}
