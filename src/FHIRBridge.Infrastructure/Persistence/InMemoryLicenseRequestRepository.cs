using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Licensing;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>No-database sample/demo mode — a single in-process slot, reset on restart same as every other
/// InMemory* repository in this mode.</summary>
public sealed class InMemoryLicenseRequestRepository : ILicenseRequestRepository
{
    private LicenseRequest? _request;

    public Task<LicenseRequest?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_request);

    public Task AddAsync(LicenseRequest request, CancellationToken cancellationToken)
    {
        _request = request;
        return Task.CompletedTask;
    }

    public Task SaveAsync(LicenseRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}
