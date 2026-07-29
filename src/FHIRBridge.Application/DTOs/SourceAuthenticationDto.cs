using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record SourceAuthenticationDto(
    AuthenticationType AuthenticationType,
    string? ClientId,
    string? TokenEndpoint,
    string[] Scopes,
    string? ClientSecretKeyVaultName,
    string? ClientSecretName,
    string? PrivateKeyKeyVaultName,
    string? PrivateKeySecretName,
    string? KeyId,
    string? JwksUrl = null);
