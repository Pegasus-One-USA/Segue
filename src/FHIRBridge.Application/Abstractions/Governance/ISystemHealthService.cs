using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>Live health for components genuinely observable from this host — see ComponentHealthDto's remarks.</summary>
public interface ISystemHealthService
{
    Task<SystemHealthDto> GetSystemHealthAsync(CancellationToken cancellationToken);
}
