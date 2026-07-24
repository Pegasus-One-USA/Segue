using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IArtifactDeliveryStrategyFactory
{
    IArtifactDeliveryStrategy Create(ArtifactDeliveryMode mode);
}

/// <summary>
/// Associates an <see cref="ArtifactDeliveryMode"/> with the concrete <see cref="IArtifactDeliveryStrategy"/> type
/// that handles it. Mirrors <c>ConfiguredDestinationWriterRegistration</c> — new delivery modes are added here via
/// dependency injection, never via a switch on the mode.
/// </summary>
public sealed record ArtifactDeliveryStrategyRegistration(ArtifactDeliveryMode Mode, Type ImplementationType);
