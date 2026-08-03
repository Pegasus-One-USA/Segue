using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No database configured — reports only the process row; there is no SQL Server to probe.</summary>
public sealed class InMemorySystemHealthService : ISystemHealthService
{
    private readonly IProcessHealthProvider _processHealthProvider;

    public InMemorySystemHealthService(IProcessHealthProvider processHealthProvider)
    {
        _processHealthProvider = processHealthProvider;
    }

    public Task<SystemHealthDto> GetSystemHealthAsync(CancellationToken cancellationToken)
    {
        var snapshot = _processHealthProvider.GetSnapshot();
        var component = new ComponentHealthDto(
            "This process (Api/Worker)",
            "Healthy",
            snapshot.CpuPercent,
            snapshot.WorkingSetBytes,
            snapshot.CpuPercent is null ? "First sample — CPU% needs a second request to establish a baseline." : null);

        return Task.FromResult(new SystemHealthDto([component]));
    }
}
