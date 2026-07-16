using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateDestinationConfigurationRequest(
    string Name,
    DestinationType DestinationType,
    string KeyVaultName,
    string SecretName,
    string? Target,
    // Option B (B1): when set, the raw connection secret (SQL connection string, or an sftp://user:pass@host:port/path
    // URI) is provisioned encrypted at (KeyVaultName, SecretName); the entity stores only the reference. When null,
    // the reference is used as-is (operator provisioned the secret out-of-band).
    string? InlineSecret = null,
    // Non-secret connection fields (server/database/schema/... for SQL; folder/delimiter/... for CSV/SFTP) as a flat
    // JSON object — see DestinationConfiguration.ConnectionMetadataJson. On update, null preserves whatever metadata
    // is already saved (ConfigurationService.UpdateDestinationConfigurationAsync only overwrites when non-null).
    string? ConnectionMetadataJson = null);
