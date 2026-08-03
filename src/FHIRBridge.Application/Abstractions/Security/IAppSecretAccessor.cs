using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Synchronous, in-process access to app-level cryptographic secrets (JWT signing key, download-link
/// signing secret) that are resolved-or-generated once at startup via <see cref="ISecretProvider"/>/
/// <see cref="ISecretWriter"/> and cached here, so request-handling code never awaits secret-store I/O.
/// </summary>
public interface IAppSecretAccessor
{
    string JwtSigningKey { get; }

    string DownloadLinkSigningSecret { get; }

    /// <summary>
    /// Updates the cached value for one app secret in THIS process only, after <see cref="ISecretWriter"/> has
    /// already persisted it — see <c>AppSecretsAdminService</c>'s remarks on the cross-process caveat for
    /// multi-instance deployments (the Worker caches the download-link secret independently).
    /// </summary>
    void Update(SecretReference secretReference, string newValue);
}
