using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Runs a real SMART Backend Services token exchange (client_credentials + signed JWT) against Epic using a
/// signing key already provisioned into the secret store, and reports the scopes Epic actually granted. Pre-create
/// — no persisted <c>SourceConnection</c> is required, so the wizard can call it as soon as a client id, token
/// endpoint, and signing key are on screen.
/// </summary>
public interface IBackendAuthScopeProbeService
{
    Task<BackendAuthScopesResult> ProbeGrantedScopesAsync(BackendAuthScopesRequest request, CancellationToken cancellationToken);
}
