using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Applications;

/// <summary>
/// Resolves the <see cref="ISourceApplicationStrategy"/> registered for an <see cref="ApplicationType"/>. This is the
/// only place application-type dispatch happens; callers resolve a strategy rather than switching on the enum.
/// </summary>
public interface ISourceApplicationStrategyRegistry
{
    /// <summary>Resolves the strategy for a type; throws if none is registered.</summary>
    ISourceApplicationStrategy Resolve(ApplicationType applicationType);

    /// <summary>Resolves the strategy for a type without throwing.</summary>
    bool TryResolve(ApplicationType applicationType, out ISourceApplicationStrategy strategy);
}
