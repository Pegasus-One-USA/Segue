namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Resolves the <see cref="IFabricLandingStrategy"/> registered for a <see cref="FabricLandingMode"/>. The only
/// place landing-mode dispatch happens; callers resolve a strategy rather than switching on the enum.
/// </summary>
public interface IFabricLandingStrategyRegistry
{
    /// <summary>Resolves the strategy for a mode; throws with the implemented modes listed if none is registered.</summary>
    IFabricLandingStrategy Resolve(FabricLandingMode mode);

    /// <summary>Resolves the strategy for a mode without throwing.</summary>
    bool TryResolve(FabricLandingMode mode, out IFabricLandingStrategy strategy);
}
