namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// What a token endpoint actually did, as a category rather than as prose. This is the distinction the old
/// string-matching could not make: every non-2xx used to be described identically, so an upstream outage was
/// reported as "check the client ID and private key" — see <see cref="TokenEndpointException"/>.
/// </summary>
public enum TokenEndpointFailureKind
{
    /// <summary>
    /// The identity provider answered and deliberately refused the request (400/401/403 — bad client id, unknown
    /// key, unapproved scope). The credentials/registration really are the thing to check.
    /// </summary>
    Rejected,

    /// <summary>
    /// The identity provider answered that it is throttling us (429). Nothing is misconfigured; the request needs
    /// to be retried later.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The identity provider (or the gateway in front of it) could not serve the request at all — 5xx, or a
    /// request-timeout status. This is an outage at the source, NOT a credentials problem, and is the case that
    /// previously produced the misleading credentials advice.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The endpoint answered successfully but the payload carried no usable access token (no body, or a body with
    /// an absent/blank <c>access_token</c>). Usually a wrong token-endpoint URL pointed at something that isn't an
    /// OAuth2 token endpoint at all.
    /// </summary>
    EmptyResponse,
}
