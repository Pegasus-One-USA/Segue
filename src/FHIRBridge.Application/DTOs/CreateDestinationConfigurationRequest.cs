using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateDestinationConfigurationRequest(
    string Name,
    DestinationType DestinationType,
    string KeyVaultName,
    string SecretName,
    string? Target);
