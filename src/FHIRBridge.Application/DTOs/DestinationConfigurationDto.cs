using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record DestinationConfigurationDto(
    Guid Id,
    string Name,
    DestinationType DestinationType,
    string KeyVaultName,
    string SecretName,
    string? Target,
    bool IsEnabled);
