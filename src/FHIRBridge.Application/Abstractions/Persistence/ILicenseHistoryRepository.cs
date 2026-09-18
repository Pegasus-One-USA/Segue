using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface ILicenseHistoryRepository
{
    Task AddAsync(LicenseHistoryEntry entry, CancellationToken cancellationToken);

    /// <summary>Newest first — matches how an admin actually wants to read "what did we apply, and when".</summary>
    Task<IReadOnlyList<LicenseHistoryEntry>> GetAllAsync(CancellationToken cancellationToken);
}
