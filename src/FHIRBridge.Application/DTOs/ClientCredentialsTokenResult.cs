namespace FHIRBridge.Application.DTOs;

/// <summary>Discriminated result for a client-credentials token request — deliberately not an exception, so
/// the controller can return RFC 6749's generic "invalid_client" for both an unknown ClientId and a wrong
/// secret, without the two cases being distinguishable from a stack trace or exception type.</summary>
public sealed record ClientCredentialsTokenResult(bool Success, AccessTokenDto? Token)
{
    public static ClientCredentialsTokenResult Failed { get; } = new(false, null);

    public static ClientCredentialsTokenResult Issued(AccessTokenDto token) => new(true, token);
}
