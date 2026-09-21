using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface ILicenseHistoryRepository
{
    Task AddAsync(LicenseHistoryEntry entry, CancellationToken cancellationToken);

    /// <summary>Newest first, excluding soft-deleted rows — matches how an admin actually wants to read
    /// "what did we apply, and when".</summary>
    Task<IReadOnlyList<LicenseHistoryEntry>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Testing/support utility only — soft-deletes every non-deleted row (see
    /// <c>LicenseHistoryEntry.SoftDelete</c>), so it can still be recovered directly from the database later
    /// if this turns out to have been a mistake. Never called from the normal apply-a-license flow; see
    /// <c>LicenseController</c>'s dedicated "Clear License History" endpoint.</summary>
    Task ClearAllAsync(CancellationToken cancellationToken);
}
