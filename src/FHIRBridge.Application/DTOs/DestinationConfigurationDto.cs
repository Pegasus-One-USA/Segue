using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record DestinationConfigurationDto(
    Guid Id,
    string Name,
    DestinationType DestinationType,
    string KeyVaultName,
    string SecretName,
    string? Target,
    bool IsEnabled,
    // Non-secret connection fields as a flat JSON object — see DestinationConfiguration.ConnectionMetadataJson.
    // Lets a caller reusing this row via "Existing" repopulate its form without ever seeing the actual secret.
    string? ConnectionMetadataJson = null,
    DateTime? CreatedOnUtc = null,
    string? CreatedBy = null,
    DateTime? ModifiedOnUtc = null,
    string? ModifiedBy = null);
