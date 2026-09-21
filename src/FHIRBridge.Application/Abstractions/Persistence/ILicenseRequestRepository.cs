using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>This install holds at most one <see cref="LicenseRequest"/> row, ever — see that entity's own
/// remarks for why a renewal reuses the same row instead of creating a new one.</summary>
public interface ILicenseRequestRepository
{
    Task<LicenseRequest?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(LicenseRequest request, CancellationToken cancellationToken);

    /// <summary>Persists in-place changes already made to a <see cref="LicenseRequest"/> previously returned
    /// by <see cref="GetAsync"/> (e.g. after calling <c>MarkSubmitted</c>/<c>MarkFailed</c>/<c>Resubmit</c>).</summary>
    Task SaveAsync(LicenseRequest request, CancellationToken cancellationToken);
}
