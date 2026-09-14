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
    string? ModifiedBy = null,
    Guid? DeIdentificationProfileId = null);

/// <summary>
/// Body for the narrow "set this destination's de-identification profile" endpoint. A null
/// <see cref="DeIdentificationProfileId"/> clears the assignment (the UI's "None" option), which is why this is a
/// body rather than a route segment — a nullable Guid has no natural URL form.
/// </summary>
public sealed record SetDestinationDeIdentificationProfileRequest(Guid? DeIdentificationProfileId);
