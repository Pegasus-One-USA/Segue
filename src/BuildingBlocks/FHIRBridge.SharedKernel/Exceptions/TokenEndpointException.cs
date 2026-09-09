using System.Text.Json;

namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// A source's OAuth2/SMART token request failed, carrying WHAT the token endpoint did as structured data
/// (<see cref="StatusCode"/>, <see cref="Kind"/>, <see cref="OAuthError"/>) instead of only as prose inside
/// <see cref="Exception.Message"/>.
/// <para>
/// Every token provider used to throw a plain <c>InvalidOperationException</c> whose message embedded the HTTP
/// status as text, so the only way to tell a genuine credentials rejection from an upstream outage was to
/// substring-search that text for "invalid_client". Anything that didn't match — a 502, 503, 504 or 429 — fell
/// through to the same "check the client ID and private key" advice, which is actively misleading while the vendor
/// is down. <c>TokenEndpointFailureDiagnosisRule</c> now branches on <see cref="Kind"/> instead.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> deliberately keeps the historical
/// "{Provider} token endpoint returned {status} ({reason}). Response body: {body}" shape, so logs read the same as
/// before and the diagnosis rule's string-based fallback still recognizes exceptions thrown by anything not yet
/// migrated to this type. <see cref="FHIRBridgeException.UserMessage"/> is the short, client-safe sentence that
/// names the real cause — the API's global handler shows it verbatim (trusted), so it never has to survive
/// <c>SafeErrorText</c>'s length/shape filter the way a raw upstream body does.
/// </para>
/// </summary>
public sealed class TokenEndpointException : FHIRBridgeException
{
    // Matches the truncation SmartBackendServicesTokenProvider.BuildFailureMessageAsync already applied, so a vendor that
    // returns a large HTML error page can't put an unbounded payload into the log message.
    private const int MaxBodyLength = 1000;

    private TokenEndpointException(
        string message,
        string userMessage,
        string providerName,
        int? statusCode,
        TokenEndpointFailureKind kind,
        string? oauthError)
        : base(message, userMessage)
    {
        ProviderName = providerName;
        StatusCode = statusCode;
        Kind = kind;
        OAuthError = oauthError;
    }

    /// <summary>Which provider/vendor's token endpoint this was, for logs and audit text (e.g. "Epic").</summary>
    public string ProviderName { get; }

    /// <summary>The HTTP status the endpoint returned, or null when the failure wasn't a status (see
    /// <see cref="TokenEndpointFailureKind.EmptyResponse"/>).</summary>
    public int? StatusCode { get; }

    /// <summary>The categorized cause — the whole reason this type exists.</summary>
    public TokenEndpointFailureKind Kind { get; }

    /// <summary>
    /// The OAuth2 <c>error</c> code parsed out of the response body (e.g. <c>invalid_client</c>,
    /// <c>invalid_scope</c>, <c>invalid_grant</c>), or null when the body wasn't JSON or carried no such field.
    /// Read as a field rather than substring-searched, so a body that merely mentions the word somewhere else
    /// can't be mistaken for the error code itself.
    /// </summary>
    public string? OAuthError { get; }

    /// <summary>
    /// The endpoint answered with a non-success status. <paramref name="responseBody"/> is the vendor's own OAuth
    /// error response — never a token, assertion or secret, since this is only ever built from a FAILED exchange.
    /// </summary>
    public static TokenEndpointException FromResponse(
        string providerName,
        int statusCode,
        string? reasonPhrase,
        string? responseBody)
    {
        var kind = ClassifyStatus(statusCode);
        var oauthError = TryReadOAuthError(responseBody);

        var message = $"{providerName} token endpoint returned {statusCode} ({reasonPhrase}).";
        var flattenedBody = Flatten(responseBody);
        if (flattenedBody is not null)
        {
            message = $"{message} Response body: {flattenedBody}";
        }

        return new TokenEndpointException(
            message,
            BuildUserMessage(providerName, statusCode, kind, oauthError),
            providerName,
            statusCode,
            kind,
            oauthError);
    }

    /// <summary>
    /// The endpoint answered successfully but there was no usable access token in the payload.
    /// <paramref name="detail"/> is author-written text (no upstream payload), safe for logs as-is.
    /// </summary>
    public static TokenEndpointException EmptyResponse(string providerName, string detail)
    {
        return new TokenEndpointException(
            $"{providerName} token endpoint returned {detail}",
            $"The token endpoint for {providerName} did not return a usable access token. Check that the token " +
            "endpoint URL and the requested scopes on this source connection are correct.",
            providerName,
            statusCode: null,
            TokenEndpointFailureKind.EmptyResponse,
            oauthError: null);
    }

    /// <summary>
    /// 429 is throttling and 5xx/408 are the endpoint failing to serve at all; everything else that reached this
    /// point is a deliberate refusal. Kept deliberately coarse — the point is only to separate "they said no" from
    /// "they couldn't answer", which is the distinction the message wording turns on.
    /// </summary>
    private static TokenEndpointFailureKind ClassifyStatus(int statusCode) => statusCode switch
    {
        429 => TokenEndpointFailureKind.RateLimited,
        408 or 425 => TokenEndpointFailureKind.Unavailable,
        >= 500 => TokenEndpointFailureKind.Unavailable,
        _ => TokenEndpointFailureKind.Rejected,
    };

    /// <summary>
    /// The client-safe sentence. Only the <see cref="TokenEndpointFailureKind.Rejected"/> branch talks about
    /// credentials — which is the entire behavioural change: an outage or a throttle now says so instead of
    /// sending the operator off to re-check a client ID that was never wrong.
    /// </summary>
    private static string BuildUserMessage(
        string providerName,
        int statusCode,
        TokenEndpointFailureKind kind,
        string? oauthError)
    {
        if (kind == TokenEndpointFailureKind.Unavailable)
        {
            return $"The token endpoint for {providerName} is not available right now (HTTP {statusCode}). This is " +
                   "an outage or maintenance window at the source, not a problem with this connection's " +
                   "credentials — try again shortly.";
        }

        if (kind == TokenEndpointFailureKind.RateLimited)
        {
            return $"{providerName} is rate-limiting FHIRBridge (HTTP {statusCode}). The credentials are fine — " +
                   "retry shortly, or reduce how often this workflow runs.";
        }

        // A parsed OAuth error code is the vendor naming the specific refusal, so it earns specific advice.
        return oauthError switch
        {
            "invalid_client" =>
                $"{providerName} rejected this app's identity (invalid_client). Check the client ID and the " +
                "signing key/secret configured on this source connection, and that the key is registered with " +
                "the vendor.",
            "invalid_scope" =>
                $"{providerName} rejected the requested scopes (invalid_scope). Check the scopes configured on " +
                "this source connection against what the app registration was approved for.",
            "invalid_grant" =>
                $"{providerName} rejected the grant (invalid_grant). For an interactive connection the stored " +
                "session has most likely expired and needs a fresh sign-in.",
            "unauthorized_client" =>
                $"{providerName} says this app is not authorized for the grant type it used " +
                "(unauthorized_client). Check the app registration's approved grant/authentication method.",
            _ =>
                $"{providerName} rejected this token request (HTTP {statusCode}). Check the token endpoint URL, " +
                "client credentials, and scopes configured on this source connection.",
        };
    }

    /// <summary>
    /// Reads the OAuth2 <c>error</c> member if the body is a JSON object with one. Never throws — a vendor is free
    /// to return HTML, plain text, or nothing at all, and none of those should turn into a second failure while
    /// we're already building the first one's message.
    /// </summary>
    private static string? TryReadOAuthError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = error.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Flatten(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var flattened = responseBody.ReplaceLineEndings(" ").Trim();
        return flattened.Length > MaxBodyLength
            ? flattened[..MaxBodyLength] + "..."
            : flattened;
    }
}
