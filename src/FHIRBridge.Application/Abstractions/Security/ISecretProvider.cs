using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Security;

public interface ISecretProvider
{
    Task<string> GetSecretAsync(SecretReference secretReference, CancellationToken cancellationToken);
}
