using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Destinations.Delivery;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Resolves the <see cref="IArtifactDeliveryStrategy"/> registered for an <see cref="ArtifactDeliveryMode"/>.
/// Mirrors <see cref="ConfiguredDestinationWriterFactory"/> exactly — adding a delivery mode is a DI registration,
/// never a switch.
/// </summary>
public sealed class ArtifactDeliveryStrategyFactory : IArtifactDeliveryStrategyFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<ArtifactDeliveryMode, Type> _registry;

    public ArtifactDeliveryStrategyFactory(
        IServiceProvider serviceProvider,
        IEnumerable<ArtifactDeliveryStrategyRegistration> registrations)
    {
        _serviceProvider = serviceProvider;
        _registry = BuildRegistry(registrations);
    }

    public IArtifactDeliveryStrategy Create(ArtifactDeliveryMode mode)
    {
        if (!_registry.TryGetValue(mode, out var implementationType))
        {
            throw new NotSupportedException($"Artifact delivery mode '{mode}' is not supported.");
        }

        return (IArtifactDeliveryStrategy)_serviceProvider.GetRequiredService(implementationType);
    }

    public static IReadOnlyList<ArtifactDeliveryStrategyRegistration> DefaultRegistrations { get; } =
    [
        new(ArtifactDeliveryMode.Download, typeof(DownloadDeliveryStrategy)),
        new(ArtifactDeliveryMode.Email, typeof(EmailDeliveryStrategy)),
        new(ArtifactDeliveryMode.Sftp, typeof(SftpDeliveryStrategy)),
        new(ArtifactDeliveryMode.DownloadUrl, typeof(DownloadUrlDeliveryStrategy))
    ];

    private static IReadOnlyDictionary<ArtifactDeliveryMode, Type> BuildRegistry(
        IEnumerable<ArtifactDeliveryStrategyRegistration> registrations)
    {
        var registry = new Dictionary<ArtifactDeliveryMode, Type>();

        foreach (var registration in registrations)
        {
            if (!registry.TryAdd(registration.Mode, registration.ImplementationType))
            {
                throw new InvalidOperationException(
                    $"More than one artifact delivery strategy is registered for mode '{registration.Mode}'.");
            }
        }

        return registry;
    }
}
