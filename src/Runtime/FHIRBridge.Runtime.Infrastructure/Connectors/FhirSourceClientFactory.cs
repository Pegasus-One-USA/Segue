using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Resolves the <see cref="IFhirSourceClient"/> registered for a <see cref="RuntimeSourceType"/>.
/// The supported types come from injected <see cref="FhirSourceClientRegistration"/> entries, so adding a new
/// source only requires a registration in dependency injection — this factory is closed for modification.
/// </summary>
public sealed class FhirSourceClientFactory : IFhirSourceClientFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<RuntimeSourceType, Type> _registry;

    public FhirSourceClientFactory(
        IServiceProvider serviceProvider,
        IEnumerable<FhirSourceClientRegistration> registrations)
    {
        _serviceProvider = serviceProvider;
        _registry = BuildRegistry(registrations);
    }

    public IFhirSourceClient Create(RuntimeSourceType sourceType)
    {
        if (!_registry.TryGetValue(sourceType, out var implementationType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceType),
                sourceType,
                "FHIR source type is not supported.");
        }

        return (IFhirSourceClient)_serviceProvider.GetRequiredService(implementationType);
    }

    /// <summary>
    /// The Phase 1 source clients. Shared by dependency injection and tests so the mapping has a single source of truth.
    /// </summary>
    public static IReadOnlyList<FhirSourceClientRegistration> DefaultRegistrations { get; } =
    [
        new(RuntimeSourceType.Epic, typeof(EpicFhirSourceClient)),
        new(RuntimeSourceType.Sample, typeof(SampleFhirSourceClient)),
        new(RuntimeSourceType.GenericFhir, typeof(EpicFhirSourceClient)),
        new(RuntimeSourceType.Athenahealth, typeof(AthenahealthFhirSourceClient)),
        // GATED (SQL/CSV phase): only Epic + Sample + GenericFhir sources are enabled. The other vendors reuse the
        // same paginated search client (the access-token grant is selected by the composite token provider per
        // source); re-enable them here once the generic Source hierarchy + ApplicationType axis land.
        // new(RuntimeSourceType.Cerner, typeof(EpicFhirSourceClient)),
        // new(RuntimeSourceType.Allscripts, typeof(EpicFhirSourceClient)),
        // new(RuntimeSourceType.Healow, typeof(EpicFhirSourceClient)),
        // new(RuntimeSourceType.MeditechGreenfield, typeof(EpicFhirSourceClient))
    ];

    private static IReadOnlyDictionary<RuntimeSourceType, Type> BuildRegistry(
        IEnumerable<FhirSourceClientRegistration> registrations)
    {
        var registry = new Dictionary<RuntimeSourceType, Type>();

        foreach (var registration in registrations)
        {
            if (!registry.TryAdd(registration.SourceType, registration.ImplementationType))
            {
                throw new InvalidOperationException(
                    $"More than one FHIR source client is registered for source type '{registration.SourceType}'.");
            }
        }

        return registry;
    }
}

/// <summary>
/// Associates a <see cref="RuntimeSourceType"/> with the concrete <see cref="IFhirSourceClient"/> type that handles it.
/// </summary>
public sealed record FhirSourceClientRegistration(RuntimeSourceType SourceType, Type ImplementationType);
