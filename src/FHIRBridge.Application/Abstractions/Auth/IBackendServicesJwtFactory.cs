namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

public interface IBackendServicesJwtFactory
{
    string CreateClientAssertion(BackendServicesJwtRequest request);
}

public sealed record BackendServicesJwtRequest(
    string ClientId,
    string TokenEndpoint,
    string PrivateKeyPem,
    string? KeyId,
    TimeSpan Lifetime);
