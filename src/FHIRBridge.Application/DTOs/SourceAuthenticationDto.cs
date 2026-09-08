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
    string? JwksUrl = null,
    string[]? DiscoveredScopes = null,
    string? PracticeId = null,
    string? AuthPlacement = null,
    // The SMART authorization (browser redirect) endpoint resolved by the portal's "Discover". Persisted so
    // re-opening a saved connection shows back what was configured; the interactive sign-in flow itself still
    // re-discovers it live at authorize time. Null for Backend System (client_credentials) connections.
    string? AuthorizationEndpoint = null,
    // When set, the raw client secret is provisioned encrypted at (ClientSecretKeyVaultName, ClientSecretName) —
    // mirrors CreateDestinationConfigurationRequest.InlineSecret exactly (see AddDestinationConfigurationAsync).
    // Null (create-on-save's default for an unedited existing connection) leaves whatever secret is already
    // stored at that reference untouched.
    string? InlineClientSecret = null);
