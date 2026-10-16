using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Governance;

namespace FHIRBridge.Application.Services;

public sealed class ClientCredentialsTokenService : IClientCredentialsTokenService
{
    private readonly IApiClientRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClientCredentialsAccessTokenIssuer _tokenIssuer;
    private readonly IGovernanceLogger _governanceLogger;

    public ClientCredentialsTokenService(
        IApiClientRepository repository,
        IPasswordHasher passwordHasher,
        IClientCredentialsAccessTokenIssuer tokenIssuer,
        IGovernanceLogger governanceLogger)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _governanceLogger = governanceLogger;
    }

    public async Task<ClientCredentialsTokenResult> IssueTokenAsync(
        string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var client = await _repository.GetByClientIdAsync(clientId, cancellationToken);

        // Same generic failure for "no such client", "disabled client" and "wrong secret" — see
        // ClientCredentialsTokenResult's remarks on why these three are never distinguished to the caller.
        // IPasswordHasher.Verify is still called against a fixed dummy hash even when the client itself
        // wasn't found, so a bad clientId can't be told apart from a bad secret by response latency either.
        var secretMatches = client is not null
            ? _passwordHasher.Verify(clientSecret, client.ClientSecretHash)
            : _passwordHasher.Verify(clientSecret, DummyHashForTimingParity);

        if (client is null || !client.IsEnabled || !secretMatches)
        {
            await LogAttemptAsync(clientId, success: false, cancellationToken);
            return ClientCredentialsTokenResult.Failed;
        }

        client.RecordUsed(DateTime.UtcNow);
        await _repository.UpdateAsync(client, cancellationToken);

        var token = _tokenIssuer.Issue(client);
        await LogAttemptAsync(clientId, success: true, cancellationToken);

        return ClientCredentialsTokenResult.Issued(token);
    }

    // A fixed, precomputed PBKDF2 hash of an arbitrary value — never a real client's secret — verified against
    // on an unknown ClientId so the PBKDF2 cost is paid on every attempt, real or not.
    private const string DummyHashForTimingParity =
        "v1:350000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private async Task LogAttemptAsync(string clientId, bool success, CancellationToken cancellationToken)
    {
        try
        {
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry(
                    "ClientCredentials", success, UserEmail: clientId,
                    FailureReason: success ? null : "Invalid client id or secret, or client disabled."),
                cancellationToken);
        }
        catch
        {
            // Audit logging must never be why a legitimate token issuance fails.
        }
    }
}
