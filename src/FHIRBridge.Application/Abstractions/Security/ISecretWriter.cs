using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Writes (provisions) a secret value for a <see cref="SecretReference"/>. Paired with <see cref="ISecretProvider"/>:
/// callers store only the reference on their entity; the actual value is provisioned here and resolved later by the
/// provider. Used by builder create-on-save so a wizard-entered connection string / password becomes a stored secret.
/// </summary>
public interface ISecretWriter
{
    Task WriteSecretAsync(SecretReference secretReference, string secretValue, CancellationToken cancellationToken);
}
