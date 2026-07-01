using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// Builds an <see cref="ApplicationType"/>→strategy map from the injected strategies, so resolution is a dictionary
/// lookup and this registry is closed for modification: a new application type is a new
/// <see cref="ISourceApplicationStrategy"/> plus one registration. Mirrors <c>FhirSourceClientFactory</c>.
/// </summary>
public sealed class SourceApplicationStrategyRegistry : ISourceApplicationStrategyRegistry
{
    private readonly IReadOnlyDictionary<ApplicationType, ISourceApplicationStrategy> _strategies;

    public SourceApplicationStrategyRegistry(IEnumerable<ISourceApplicationStrategy> strategies)
    {
        var map = new Dictionary<ApplicationType, ISourceApplicationStrategy>();
        foreach (var strategy in strategies)
        {
            if (!map.TryAdd(strategy.Handles, strategy))
            {
                throw new InvalidOperationException(
                    $"More than one source application strategy is registered for application type '{strategy.Handles}'.");
            }
        }

        _strategies = map;
    }

    public ISourceApplicationStrategy Resolve(ApplicationType applicationType)
    {
        if (!_strategies.TryGetValue(applicationType, out var strategy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(applicationType),
                applicationType,
                "No source application strategy is registered for this application type.");
        }

        return strategy;
    }

    public bool TryResolve(ApplicationType applicationType, out ISourceApplicationStrategy strategy)
    {
        return _strategies.TryGetValue(applicationType, out strategy!);
    }
}
