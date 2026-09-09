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
    string? ConnectionMetadataJson = null,
    // Which DeIdentificationProfile applies to this destination — null means no de-identification.
    Guid? DeIdentificationProfileId = null,
    // Set when this request is forking a brand-new connection off an existing one the user picked but then
    // edited (the workflow wizard never mutates a shared connection in place) — lets
    // ConfigurationService.AddDestinationConfigurationAsync resolve this destination's own already-stored
    // secret and inherit its credentials into InlineSecret when the latter is missing them (e.g. the user only
    // toggled "Require SSL" and never intended to change the password). See ISqlConnectionSecretMerger.
    Guid? InheritSecretFromDestinationId = null);
