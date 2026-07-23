namespace FHIRBridge.Application.DTOs;

/// <summary>Raw PEM text of a customer-supplied RSA private key, read client-side from an uploaded file.</summary>
public sealed record ImportSigningKeyRequest(string PrivateKeyPem);
