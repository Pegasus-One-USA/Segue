using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Resolves the <see cref="IMappingSchemaProvider"/> registered for a <see cref="DestinationType"/>. Mirrors
/// <see cref="ConfiguredDestinationWriterFactory"/>: supported destinations come from injected
/// <see cref="MappingSchemaProviderRegistration"/> entries, so adding a destination only requires a
/// registration in dependency injection — this factory stays closed for modification.
/// </summary>
public sealed class MappingSchemaProviderFactory : IMappingSchemaProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<DestinationType, Type> _registry;

    public MappingSchemaProviderFactory(
        IServiceProvider serviceProvider,
        IEnumerable<MappingSchemaProviderRegistration> registrations)
    {
        _serviceProvider = serviceProvider;
        _registry = BuildRegistry(registrations);
    }

    public IMappingSchemaProvider Create(DestinationType destinationType)
    {
        if (!_registry.TryGetValue(destinationType, out var implementationType))
        {
            throw new NotSupportedException(
                $"Destination type '{destinationType}' does not support mapping schema import yet.");
        }

        return (IMappingSchemaProvider)_serviceProvider.GetRequiredService(implementationType);
    }

    /// <summary>
    /// The mapping-import schema providers. Shared by dependency injection and tests so the mapping is
    /// declared in exactly one place.
    /// </summary>
    public static IReadOnlyList<MappingSchemaProviderRegistration> DefaultRegistrations { get; } =
    [
        new(DestinationType.SqlServer, typeof(SqlServerMappingSchemaProvider)),
        new(DestinationType.AzureSql, typeof(SqlServerMappingSchemaProvider)),
        new(DestinationType.MySql, typeof(MySqlMappingSchemaProvider)),
        new(DestinationType.PostgreSql, typeof(PostgreSqlMappingSchemaProvider))
    ];

    private static IReadOnlyDictionary<DestinationType, Type> BuildRegistry(
        IEnumerable<MappingSchemaProviderRegistration> registrations)
    {
        var registry = new Dictionary<DestinationType, Type>();

        foreach (var registration in registrations)
        {
            if (!registry.TryAdd(registration.DestinationType, registration.ImplementationType))
            {
                throw new InvalidOperationException(
                    $"More than one mapping schema provider is registered for destination type '{registration.DestinationType}'.");
            }
        }

        return registry;
    }
}

/// <summary>
/// Associates a <see cref="DestinationType"/> with the concrete <see cref="IMappingSchemaProvider"/> type that handles it.
/// </summary>
public sealed record MappingSchemaProviderRegistration(DestinationType DestinationType, Type ImplementationType);
