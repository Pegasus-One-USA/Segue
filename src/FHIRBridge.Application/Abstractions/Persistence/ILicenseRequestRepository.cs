using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>This install can hold any number of <see cref="LicenseRequest"/> rows — one per submission
/// (each with its own <see cref="LicenseRequest.UniqueKey"/>), not one per install. A resend/edit acts on a
/// specific existing row by <see cref="LicenseRequest.Id"/> rather than creating a new one; only a brand
/// new "Submit Request" click creates a fresh row.</summary>
public interface ILicenseRequestRepository
{
    Task<IReadOnlyList<LicenseRequest>> ListAsync(CancellationToken cancellationToken);

    Task<LicenseRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Used only by <c>LicenseService.ApplyAsync</c> to confirm a license's <c>requestKey</c> claim
    /// matches one of THIS install's own past requests, proving it wasn't minted for (or copied from) a
    /// different install.</summary>
    Task<LicenseRequest?> GetByUniqueKeyAsync(string uniqueKey, CancellationToken cancellationToken);

    Task AddAsync(LicenseRequest request, CancellationToken cancellationToken);

    /// <summary>Persists in-place changes already made to a <see cref="LicenseRequest"/> previously returned
    /// by <see cref="GetAsync"/> (e.g. after calling <c>MarkSubmitted</c>/<c>MarkFailed</c>/<c>Resubmit</c>/
    /// <c>UpdateDetails</c>).</summary>
    Task SaveAsync(LicenseRequest request, CancellationToken cancellationToken);
}
