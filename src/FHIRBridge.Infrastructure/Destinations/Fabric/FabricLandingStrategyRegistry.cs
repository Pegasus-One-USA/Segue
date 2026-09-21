namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Builds a <see cref="FabricLandingMode"/>→strategy map from the injected strategies, so resolution is a
/// dictionary lookup and this registry is closed for modification: a new landing surface is a new
/// <see cref="IFabricLandingStrategy"/> plus one DI registration. Mirrors
/// <c>SourceApplicationStrategyRegistry</c>.
/// </summary>
internal sealed class FabricLandingStrategyRegistry : IFabricLandingStrategyRegistry
{
    private readonly IReadOnlyDictionary<FabricLandingMode, IFabricLandingStrategy> _strategies;

    public FabricLandingStrategyRegistry(IEnumerable<IFabricLandingStrategy> strategies)
    {
        var map = new Dictionary<FabricLandingMode, IFabricLandingStrategy>();
        foreach (var strategy in strategies)
        {
            if (!map.TryAdd(strategy.Handles, strategy))
            {
                throw new InvalidOperationException(
                    $"More than one Fabric landing strategy is registered for landing mode '{strategy.Handles}'.");
            }
        }

        _strategies = map;
    }

    public IFabricLandingStrategy Resolve(FabricLandingMode mode)
    {
        if (!_strategies.TryGetValue(mode, out var strategy))
        {
            // Naming what IS implemented matters more than naming what isn't: Eventstream is deliberately absent
            // (it is authenticated HTTP, already served by the Data Lake Webhook destination), so a user who lands
            // here needs to know where to go rather than be told to wait for a build.
            throw new NotSupportedException(
                $"Fabric landing mode '{mode}' is not implemented. Implemented modes: "
                    + string.Join(", ", _strategies.Keys.Order()) + ".");
        }

        return strategy;
    }

    public bool TryResolve(FabricLandingMode mode, out IFabricLandingStrategy strategy)
    {
        return _strategies.TryGetValue(mode, out strategy!);
    }
}
