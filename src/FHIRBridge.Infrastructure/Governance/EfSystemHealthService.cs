using System.Diagnostics;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Reports the current process's live CPU/memory (<see cref="IProcessHealthProvider"/>) plus a SQL Server
/// connectivity probe (a real round-trip, timed) — only components this host can genuinely observe. A remote
/// Worker process is deliberately not reported here: this host has no way to measure it, and a placeholder row
/// would misrepresent that as monitored.
/// </summary>
public sealed class EfSystemHealthService : ISystemHealthService
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IProcessHealthProvider _processHealthProvider;

    public EfSystemHealthService(FHIRBridgeDbContext dbContext, IProcessHealthProvider processHealthProvider)
    {
        _dbContext = dbContext;
        _processHealthProvider = processHealthProvider;
    }

    public async Task<SystemHealthDto> GetSystemHealthAsync(CancellationToken cancellationToken)
    {
        var processSnapshot = _processHealthProvider.GetSnapshot();
        var processComponent = new ComponentHealthDto(
            "This process (Api/Worker)",
            "Healthy",
            processSnapshot.CpuPercent,
            processSnapshot.WorkingSetBytes,
            processSnapshot.CpuPercent is null ? "First sample — CPU% needs a second request to establish a baseline." : null);

        var stopwatch = Stopwatch.StartNew();
        ComponentHealthDto sqlComponent;
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            stopwatch.Stop();
            sqlComponent = new ComponentHealthDto(
                "SQL Server",
                canConnect ? "Healthy" : "Offline",
                null,
                null,
                $"Connectivity check: {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            sqlComponent = new ComponentHealthDto("SQL Server", "Offline", null, null, exception.Message);
        }

        return new SystemHealthDto([processComponent, sqlComponent]);
    }
}
