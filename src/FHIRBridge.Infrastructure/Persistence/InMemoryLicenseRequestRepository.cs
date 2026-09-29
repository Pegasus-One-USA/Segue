using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>No-database sample/demo mode — an in-process list, reset on restart same as every other
/// InMemory* repository in this mode.</summary>
public sealed class InMemoryLicenseRequestRepository : ILicenseRequestRepository
{
    private readonly List<LicenseRequest> _requests = new();

    public Task<IReadOnlyList<LicenseRequest>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LicenseRequest>>(
            _requests.Where(r => !r.IsDeleted).OrderByDescending(r => r.CreatedUtc).ToList());

    public Task<LicenseRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_requests.FirstOrDefault(r => r.Id == id));

    public Task<LicenseRequest?> GetByUniqueKeyAsync(string uniqueKey, CancellationToken cancellationToken) =>
        Task.FromResult(_requests.FirstOrDefault(r => r.UniqueKey == uniqueKey));

    public Task AddAsync(LicenseRequest request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        return Task.CompletedTask;
    }

    public Task SaveAsync(LicenseRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}
