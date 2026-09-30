using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// One allowed redirect target for an <see cref="ApiClient"/> — the counterpart of
/// <see cref="AllowedCorsOrigin"/> for the browser-redirect external-trigger flow
/// (<c>POST /api/v1/workflows/external/run</c>). Unlike an <see cref="AllowedCorsOrigin"/> (scheme+host+port
/// only, since it gates a browser's CORS-scoped fetch), this is matched as a full absolute URL including
/// path: it is the caller's own return page, not a browser origin permitted to call the API, and exact match
/// (no prefix/wildcard) is what keeps a leaked Client ID/Secret from being used to redirect a triggered
/// workflow's outcome to an attacker-controlled page. Its origin (scheme+host+port) doubles as the allowed
/// Referer set for the same request — see <c>ExternalWorkflowTriggerService</c>.
/// </summary>
public sealed class ApiClientReturnUrl : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private ApiClientReturnUrl()
    {
    }

    public ApiClientReturnUrl(Guid apiClientId, string url, string? label, ReturnUrlMatchMode matchMode = ReturnUrlMatchMode.Exact)
    {
        Id = Guid.NewGuid();
        ApiClientId = apiClientId;
        Url = url;
        Label = label;
        MatchMode = matchMode;
    }

    public Guid ApiClientId { get; private set; }

    /// <summary>
    /// For <see cref="ReturnUrlMatchMode.Exact"/>: the full absolute URL — scheme+host+path, no query/fragment,
    /// matched exactly, never as a prefix. For <see cref="ReturnUrlMatchMode.Domain"/>: just the origin
    /// (scheme+host+port, e.g. <c>https://app.example.com</c>) — the caller's Return URL may then be any path
    /// under that origin.
    /// </summary>
    public string Url { get; private set; } = default!;

    public string? Label { get; private set; }

    public ReturnUrlMatchMode MatchMode { get; private set; }

    string? IHasAuditDisplayName.AuditDisplayName => Label ?? Url;
}

/// <summary>How a caller-supplied Return URL is checked against a registered <see cref="ApiClientReturnUrl"/>.</summary>
public enum ReturnUrlMatchMode
{
    /// <summary>The Return URL must equal <see cref="ApiClientReturnUrl.Url"/> exactly (case-insensitive).</summary>
    Exact = 0,

    /// <summary>The Return URL's origin (scheme+host+port) must equal <see cref="ApiClientReturnUrl.Url"/>,
    /// which stores only that origin — any path/query/fragment on the caller's Return URL is allowed.</summary>
    Domain = 1,
}
