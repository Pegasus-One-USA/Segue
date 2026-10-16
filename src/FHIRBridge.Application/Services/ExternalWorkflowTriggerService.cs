using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Validates the browser-redirect external-trigger flow (<c>POST /api/v1/workflows/external/run</c>): unlike
/// the OAuth2 Client Credentials Grant (<see cref="ClientCredentialsTokenService"/>), the credential and the
/// redirect target both arrive in one browser-driven request, so this checks three things together —
/// <list type="bullet">
/// <item>the Client ID/Secret is valid and the client is enabled;</item>
/// <item>the caller-supplied Return URL is one this specific client pre-registered (exact match — see
/// <see cref="ApiClientReturnUrl"/>'s remarks on why this is required, not optional: an unchecked
/// caller-supplied redirect target is an open-redirect vulnerability);</item>
/// <item>when the browser sent a Referer header, its origin matches one of that same client's registered
/// return URLs' origins (soft check: a MISSING Referer is not itself a failure — real browsers often omit it
/// for privacy reasons on a cross-origin top-level navigation — but a PRESENT, mismatched one is, since that's
/// a real signal the request isn't coming from a page this client actually registered).</item>
/// </list>
/// All three fail the same generic way (<see langword="null"/>) so a caller can't distinguish "no such
/// client" from "wrong secret" from "return URL not registered" — the same reasoning
/// <see cref="ClientCredentialsTokenService"/> already applies to its own credential check.
/// </summary>
public sealed class ExternalWorkflowTriggerService : IExternalWorkflowTriggerService
{
    // Same fixed, precomputed PBKDF2 hash used for timing parity in ClientCredentialsTokenService — verified
    // against on an unknown ClientId so the PBKDF2 cost is paid on every attempt, real or not.
    private const string DummyHashForTimingParity =
        "v1:350000:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly IApiClientRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IGovernanceLogger _governanceLogger;

    public ExternalWorkflowTriggerService(
        IApiClientRepository repository, IPasswordHasher passwordHasher, IGovernanceLogger governanceLogger)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _governanceLogger = governanceLogger;
    }

    public async Task<ExternalTriggerValidationResult?> ValidateAsync(
        string clientId, string clientSecret, string returnUrl, string? refererHeader, CancellationToken cancellationToken)
    {
        var outcome = await ValidateCoreAsync(clientId, clientSecret, returnUrl, refererHeader, cancellationToken);
        return outcome.Result;
    }

    public async Task<CallerValidationOutcome> ValidateCallerAsync(
        string clientId, string clientSecret, string returnUrl, string? refererHeader, CancellationToken cancellationToken)
    {
        var outcome = await ValidateCoreAsync(clientId, clientSecret, returnUrl, refererHeader, cancellationToken);
        return new CallerValidationOutcome(outcome.Result?.ApiClientId, outcome.Error, outcome.Description);
    }

    // Credential failures stay generic (no client enumeration); once the credential itself is verified, telling the
    // caller WHICH check failed (caller URL not allowed / Referer mismatch) leaks nothing they don't already own.
    private async Task<(ExternalTriggerValidationResult? Result, string? Error, string? Description)> ValidateCoreAsync(
        string clientId, string clientSecret, string returnUrl, string? refererHeader, CancellationToken cancellationToken)
    {
        var client = await VerifyCredentialAsync(clientId, clientSecret, cancellationToken);
        if (client is null)
        {
            await LogAttemptAsync(clientId, success: false, "Invalid client id or secret, or client disabled.", cancellationToken);
            return (null, "invalid_client", null);
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri))
        {
            await LogAttemptAsync(clientId, success: false, "returnUrl is not registered for this client.", cancellationToken);
            return (null, "caller_url_not_allowed", "The caller URL is not a valid absolute URL, so it cannot be matched to this client's Allowed Caller URLs.");
        }

        // Stored values were normalized at registration (Uri.GetLeftPart), so the caller's URL is normalized the
        // same way before comparing — otherwise an equivalent spelling (default port, host/scheme case, encoding)
        // would be rejected as if it were a different URL. An exact entry still refuses a caller URL carrying a
        // query or fragment: normalizing must not quietly loosen "this exact page" into "this page plus anything".
        var returnUrlOrigin = returnUri.GetLeftPart(UriPartial.Authority);
        var returnUrlPath = returnUri.GetLeftPart(UriPartial.Path);
        var hasQueryOrFragment = !string.IsNullOrEmpty(returnUri.Query) || !string.IsNullOrEmpty(returnUri.Fragment);
        var isRegistered = client.ReturnUrls.Any(x => x.MatchMode == ReturnUrlMatchMode.Domain
            ? string.Equals(x.Url, returnUrlOrigin, StringComparison.OrdinalIgnoreCase)
            : !hasQueryOrFragment && string.Equals(x.Url, returnUrlPath, StringComparison.OrdinalIgnoreCase));

        if (!isRegistered)
        {
            await LogAttemptAsync(clientId, success: false, "returnUrl is not registered for this client.", cancellationToken);
            return (null, "caller_url_not_allowed", "The caller URL is not whitelisted: it is not in this API client's Allowed Caller URLs.");
        }

        if (!IsRefererAllowed(client, refererHeader, out var refererFailureReason))
        {
            await LogAttemptAsync(clientId, success: false, refererFailureReason, cancellationToken);
            return (null, "caller_url_not_allowed", "The request's Referer origin is not whitelisted: it does not match this API client's Allowed Caller URLs.");
        }

        await LogAttemptAsync(clientId, success: true, null, cancellationToken);
        await StampLastUsedAsync(client, cancellationToken);
        return (new ExternalTriggerValidationResult(client.Id, returnUrl), null, null);
    }

    public async Task<Guid?> ValidateCredentialAsync(
        string clientId, string clientSecret, string? refererHeader, CancellationToken cancellationToken)
    {
        var client = await VerifyCredentialAsync(clientId, clientSecret, cancellationToken);
        if (client is null)
        {
            await LogAttemptAsync(clientId, success: false, "Invalid client id or secret, or client disabled.", cancellationToken);
            return null;
        }

        if (!IsRefererAllowed(client, refererHeader, out var refererFailureReason))
        {
            await LogAttemptAsync(clientId, success: false, refererFailureReason, cancellationToken);
            return null;
        }

        await LogAttemptAsync(clientId, success: true, null, cancellationToken);
        await StampLastUsedAsync(client, cancellationToken);
        return client.Id;
    }

    /// <summary>API Clients shows "Last used"; a launch-ticket or external-trigger call counts as use just like a
    /// token request does. Best effort - a failure to record it must never fail the caller's request.</summary>
    private async Task StampLastUsedAsync(ApiClient client, CancellationToken cancellationToken)
    {
        try
        {
            client.RecordUsed(DateTime.UtcNow);
            await _repository.UpdateAsync(client, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Intentionally swallowed: bookkeeping only.
        }
    }

    /// <summary>Soft check: a MISSING Referer is not itself a failure (real browsers often omit it for privacy
    /// reasons on a cross-origin request/navigation) — but a PRESENT, mismatched one is, since that's a real
    /// signal the request isn't coming from a page this client actually registered.</summary>
    private static bool IsRefererAllowed(ApiClient client, string? refererHeader, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(refererHeader))
        {
            failureReason = null;
            return true;
        }

        if (!Uri.TryCreate(refererHeader, UriKind.Absolute, out var refererUri))
        {
            failureReason = "Referer header was present but not a valid URL.";
            return false;
        }

        var refererOrigin = refererUri.GetLeftPart(UriPartial.Authority);
        var matchesAnyRegisteredOrigin = client.ReturnUrls.Any(x =>
            Uri.TryCreate(x.Url, UriKind.Absolute, out var registeredUri)
            && string.Equals(registeredUri.GetLeftPart(UriPartial.Authority), refererOrigin, StringComparison.OrdinalIgnoreCase));

        if (!matchesAnyRegisteredOrigin)
        {
            failureReason = "Referer origin does not match any URL registered for this client.";
            return false;
        }

        failureReason = null;
        return true;
    }

    private async Task<ApiClient?> VerifyCredentialAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var client = await _repository.GetByClientIdAsync(clientId, cancellationToken);

        // Verify is called even when the client wasn't found (against a fixed dummy hash), so a bad clientId
        // can't be told apart from a bad secret by response latency — same reasoning as
        // ClientCredentialsTokenService.
        var secretMatches = client is not null
            ? _passwordHasher.Verify(clientSecret, client.ClientSecretHash)
            : _passwordHasher.Verify(clientSecret, DummyHashForTimingParity);

        return client is not null && client.IsEnabled && secretMatches ? client : null;
    }

    private async Task LogAttemptAsync(string clientId, bool success, string? failureReason, CancellationToken cancellationToken)
    {
        try
        {
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry("ExternalWorkflowTrigger", success, UserEmail: clientId, FailureReason: failureReason),
                cancellationToken);
        }
        catch
        {
            // Audit logging must never be why a legitimate trigger fails.
        }
    }
}
