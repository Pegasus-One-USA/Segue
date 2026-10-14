using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IExternalWorkflowTriggerService
{
    /// <summary>Validates clientId/clientSecret, that returnUrl is one of that client's registered return
    /// URLs, and — when refererHeader is present — that it matches one of the same client's registered
    /// origins. Returns null on any failure (generic — the caller never learns which check failed), and never
    /// throws for an invalid request; every attempt, success or failure, is audited via IGovernanceLogger
    /// without ever logging the secret.</summary>
    Task<ExternalTriggerValidationResult?> ValidateAsync(
        string clientId, string clientSecret, string returnUrl, string? refererHeader, CancellationToken cancellationToken);

    /// <summary>Same checks as ValidateAsync, but reports which one failed: "invalid_client" (bad credential,
    /// deliberately generic) or "caller_url_not_allowed" (credential fine, URL/Referer not on the allow-list).</summary>
    Task<CallerValidationOutcome> ValidateCallerAsync(
        string clientId, string clientSecret, string returnUrl, string? refererHeader, CancellationToken cancellationToken);

    /// <summary>Read-only counterpart used by the workflow/EHR-endpoint listing endpoints — validates the
    /// credential and, when refererHeader is present, applies the same soft origin check ValidateAsync does
    /// (a data-listing call is still worth confirming comes from a page this client actually registered, even
    /// though nothing here redirects). Returns the ApiClient's id on success.</summary>
    Task<Guid?> ValidateCredentialAsync(
        string clientId, string clientSecret, string? refererHeader, CancellationToken cancellationToken);
}

public sealed record CallerValidationOutcome(Guid? ApiClientId, string? Error, string? Description);
