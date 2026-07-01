using FHIRBridge.Runtime.Application.Abstractions.Destinations;
using FHIRBridge.Runtime.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.Infrastructure.Destinations;

/// <summary>
/// Resolves the <see cref="IDestinationWriter"/> registered for a <see cref="RuntimeDestinationType"/>.
/// New destinations are added through <see cref="DestinationWriterRegistration"/> entries in dependency injection,
/// leaving this factory closed for modification.
/// </summary>
public sealed class DestinationWriterFactory : IDestinationWriterFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<RuntimeDestinationType, Type> _registry;

    public DestinationWriterFactory(
        IServiceProvider serviceProvider,
        IEnumerable<DestinationWriterRegistration> registrations)
    {
        _serviceProvider = serviceProvider;
        _registry = BuildRegistry(registrations);
    }

    public IDestinationWriter Create(RuntimeDestinationType destinationType)
    {
        if (!_registry.TryGetValue(destinationType, out var implementationType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationType),
                destinationType,
                "Runtime destination type is not supported.");
        }

        return (IDestinationWriter)_serviceProvider.GetRequiredService(implementationType);
    }

    /// <summary>
    /// The Phase 1 runtime destination writers. Shared by dependency injection and tests as the single source of truth.
    /// </summary>
    public static IReadOnlyList<DestinationWriterRegistration> DefaultRegistrations { get; } =
    [
        new(RuntimeDestinationType.SqlServer, typeof(SqlServerDestinationWriter)),
        new(RuntimeDestinationType.InMemory, typeof(InMemoryDestinationWriter))
    ];

    private static IReadOnlyDictionary<RuntimeDestinationType, Type> BuildRegistry(
        IEnumerable<DestinationWriterRegistration> registrations)
    {
        var registry = new Dictionary<RuntimeDestinationType, Type>();

        foreach (var registration in registrations)
        {
            if (!registry.TryAdd(registration.DestinationType, registration.ImplementationType))
            {
                throw new InvalidOperationException(
                    $"More than one destination writer is registered for destination type '{registration.DestinationType}'.");
            }
        }

        return registry;
    }
}

/// <summary>
/// Associates a <see cref="RuntimeDestinationType"/> with the concrete <see cref="IDestinationWriter"/> type that handles it.
/// </summary>
public sealed record DestinationWriterRegistration(RuntimeDestinationType DestinationType, Type ImplementationType);
