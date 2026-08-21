namespace FHIRBridge.Application.DTOs;

public sealed record DecryptProvisionedSecretRequest(string ProtectedValue);

public sealed record DecryptProvisionedSecretResponse(string PlaintextValue);
